﻿using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Common_Utils;
using System.Linq;
using System.Text.Json;

namespace JeskeiMediaFunctions
{
    public static class SubmitSubclipJob
    {
        /// <summary>
        /// Data to pass as an input to the function
        /// </summary>
        private class RequestBodyModel
        {
            /// <summary>
            /// Signature of wallet owner to prove ownership of asset being loaded
            /// Mandatory.
            /// </summary>
            [JsonProperty("assetOwnerSignature")]
            public string AssetOwnerSignature { get; set; }

            [JsonProperty("liveEventName")]
            public string LiveEventName { get; set; }

            [JsonProperty("liveOutputName")]
            public string LiveOutputName { get; set; }

            [System.Text.Json.Serialization.JsonConverterAttribute(typeof(TimeSpanConverter))]
            [JsonProperty("lastSubclipEndTime")]
            public TimeSpan? LastSubclipEndTime { get; set; }

            [JsonProperty("outputAssetStorageAccount")]
            public string OutputAssetStorageAccount { get; set; }

            [JsonProperty("intervalSec")]
            public int? IntervalSec { get; set; }
        }

        /// <summary>
        /// Data output by the function
        /// </summary>
        private class AnswerBodyModel
        {
            [JsonProperty("subclipAssetName")]
            public string SubclipAssetName { get; set; }

            [JsonProperty("subclipJobName")]
            public string SubclipJobName { get; set; }

            [JsonProperty("subclipTransformName")]
            public string SubclipTransformName { get; set; }

            [System.Text.Json.Serialization.JsonConverterAttribute(typeof(TimeSpanConverter))]
            [JsonProperty("subclipEndTime")]
            public TimeSpan SubclipEndTime { get; set; }
        }

        private const string SubclipTransformName = "FunctionSubclipTransform";

        /// <summary>
        /// Function which submits a subclipping job for a live output / asset
        /// </summary>
        /// <param name="req"></param>
        /// <param name="executionContext"></param>
        /// <returns></returns>
        [FunctionName("SubmitSubclipJob")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string triggerStart = DateTime.UtcNow.ToString("yyMMddHHmmss");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);


            // Return bad request if input asset name is not passed in
            if (data.LiveEventName == null || data.LiveOutputName == null)
            {
                return new BadRequestObjectResult("Please pass liveEventName and liveOutputName in the request body");
            }

            data.IntervalSec ??= 60;

            ConfigWrapper config = ConfigUtils.GetConfig();

            IAzureMediaServicesClient client;
            try
            {
                client = await Authentication.CreateMediaServicesClientAsync(config);
            }
            catch (Exception e)
            {
                if (e.Source.Contains("ActiveDirectory"))
                {
                    log.LogError("TIP: Make sure that you have filled out the appsettings.json file before running this sample.");
                }
                log.LogError($"{e.Message}");

                return new BadRequestObjectResult(e.Message);
            }

            // Set the polling interval for long running operations to 2 seconds.
            // The default value is 30 seconds for the .NET client SDK
            client.LongRunningOperationRetryTimeout = 2;

            try
            {
                // Ensure that you have customized encoding Transform.  This is really a one time setup operation.
                Transform transform = await TransformUtils.GetOrCreateSubclipTransform(client, log, config.ResourceGroup, config.AccountName, SubclipTransformName);
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when  getting or creating the transform");
            }

            string liveEventName = req.Query["liveEventName"];
            string liveOutputName = req.Query["liveOutputName"];

            var liveOutput = await client.LiveOutputs.GetAsync(config.ResourceGroup, config.AccountName, liveEventName, liveOutputName);


            // let's analyze the client manifest and adjust times for the subclip job
            var doc = await LiveManifest.TryToGetClientManifestContentAsABlobAsync(client, config.ResourceGroup, config.AccountName, liveOutput.AssetName);
            var assetmanifestdata = LiveManifest.GetManifestTimingData(doc);

            if (assetmanifestdata.Error)
            {
                return new BadRequestObjectResult("Data cannot be read from live output / asset manifest.");
            }

            log.LogInformation("Timestamps : " + string.Join(",", assetmanifestdata.TimestampList.Select(n => n.ToString()).ToArray()));

            var livetime = TimeSpan.FromSeconds(assetmanifestdata.TimestampEndLastChunk / (double)assetmanifestdata.TimeScale);

            log.LogInformation($"Livetime : {livetime}");

            var starttime = LiveManifest.ReturnTimeSpanOnGOP(assetmanifestdata, livetime.Subtract(TimeSpan.FromSeconds((int)data.IntervalSec)));
            log.LogInformation($"Value starttime : {starttime}");

            if (data.LastSubclipEndTime != null)
            {
                var lastEndTime = (TimeSpan)data.LastSubclipEndTime;
                log.LogInformation($"Value lastEndTime : {lastEndTime}");

                var delta = (livetime - lastEndTime - TimeSpan.FromSeconds((int)data.IntervalSec)).Duration();
                log.LogInformation($"Delta: {delta}");

                if (delta < (TimeSpan.FromSeconds(3 * (int)data.IntervalSec))) // less than 3 times the normal duration (3*60s)
                {
                    starttime = lastEndTime;
                    log.LogInformation($"Value new starttime : {starttime}");
                }
            }

            var duration = livetime - starttime;
            log.LogInformation($"Value duration: {duration}");
            if (duration == new TimeSpan(0)) // Duration is zero, this may happen sometimes !
            {
                return new BadRequestObjectResult("Stopping. Duration of subclip is zero.");
            }

            Asset outputAsset;
            try
            {
                // Output from the Job must be written to an Asset, so let's create one
                outputAsset = await AssetUtils.CreateAssetAsync(client, log, config.ResourceGroup, config.AccountName, liveOutput.Name + "-subclip-" + triggerStart, data.OutputAssetStorageAccount);
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when creating the output asset.");
            }

            JobInput jobInput = new JobInputAsset(
                assetName: liveOutput.AssetName,
                start: new AbsoluteClipTime(starttime.Subtract(TimeSpan.FromMilliseconds(100))),
                end: new AbsoluteClipTime(livetime.Add(TimeSpan.FromMilliseconds(100)))
                );

            Job job;
            try
            {
                job = await JobUtils.SubmitJobAsync(
             client,
             log,
             config.ResourceGroup,
             config.AccountName,
             SubclipTransformName,
             $"Subclip-{liveOutput.Name}-{triggerStart}",
             jobInput,
             outputAsset.Name
             );
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when submitting the job.");
            }


            AnswerBodyModel dataOk = new()
            {
                SubclipAssetName = outputAsset.Name,
                SubclipJobName = job.Name,
                SubclipTransformName = SubclipTransformName,
                SubclipEndTime = starttime + duration
            };

            return new OkObjectResult(dataOk);
        }
    }

    public class TimeSpanConverter : System.Text.Json.Serialization.JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return TimeSpan.Parse(reader.GetString());
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
