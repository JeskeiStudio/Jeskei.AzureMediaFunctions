using System;
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
using System.Collections.Generic;

namespace JeskeiMediaFunctions
{
    public static class PublishAsset
    {
        /// <summary>
        /// Data to pass as an input to the function
        /// </summary>
        private class RequestBodyModel
        {
            /// <summary>
            /// Name of the asset to publish.
            /// Mandatory.
            /// </summary>
            [JsonProperty("assetName")]
            public string AssetName { get; set; }

            /// <summary>
            /// Signature of wallet owner to prove ownership of asset being loaded
            /// Mandatory.
            /// </summary>
            [JsonProperty("assetOwnerSignature")]
            public string AssetOwnerSignature { get; set; }

            /// <summary>
            /// Streaming policy name
            /// Mandatory.
            /// You can either create one with `CreateStreamingPolicy` or use any of the predefined policies:
            /// Predefined_ClearKey,
            /// Predefined_ClearStreamingOnly,
            /// Predefined_DownloadAndClearStreaming,
            /// Predefined_DownloadOnly,
            /// Predefined_MultiDrmCencStreaming,
            /// Predefined_MultiDrmStreaming.
            /// </summary>
            [JsonProperty("streamingPolicyName")]
            public string StreamingPolicyName { get; set; }

            /// <summary>
            /// Content key policy name
            /// Optional.
            /// </summary>
            [JsonProperty("contentKeyPolicyName")]
            public string ContentKeyPolicyName { get; set; }

            /// <summary>
            /// Streaming locator Id.
            /// For example "911b65de-ac92-4391-9aab-80021126d403"
            /// Optional.
            /// </summary>
            [JsonProperty("streamingLocatorId")]
            public string StreamingLocatorId { get; set; }

            /// <summary>
            /// Start time of the locator
            /// Optional.
            /// </summary>
            [JsonProperty("startDateTime")]
            public DateTime? StartDateTime { get; set; }

            /// <summary>
            /// End time of the locator
            /// Optional.
            /// </summary>
            [JsonProperty("endDateTime")]
            public DateTime? EndDateTime { get; set; }

            /// <summary>
            /// JSON string with the content keys to be used by the streaming locator.
            /// Use @{url} to load from a file from the specified URL.
            /// For further information about the JSON structure please refer to swagger documentation on
            /// https://docs.microsoft.com/en-us/rest/api/media/streaminglocators/create#streaminglocatorcontentkey.
            /// Optional.
            /// </summary>
            [JsonProperty("contentKeys")]
            public string ContentKeys { get; set; }
        }

        /// <summary>
        /// Data output by the function
        /// </summary>
        private class AnswerBodyModel
        {
            /// <summary>
            /// Name of the streaming locator created.
            /// </summary>
            [JsonProperty("streamingLocatorName")]
            public string StreamingLocatorName { get; set; }

            /// <summary>
            /// Id of the locator.
            /// </summary>
            [JsonProperty("streamingLocatorId")]
            public Guid StreamingLocatorId { get; set; }
        }

        /// <summary>
        /// Function which publishes an asset.
        /// </summary>
        /// <param name="req"></param>
        /// <param name="executionContext"></param>
        /// <returns></returns>
        [FunctionName("PublishAsset")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string assetName = req.Query["assetNamePrefix"];
            string streamingPolicyName = req.Query["streamingPolicyName"];
            string contentKeyPolicyName = req.Query["contentKeyPolicyName"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            assetName = assetName ?? data?.assetName;

            if (assetName == null)
            {
                return new OkObjectResult("Please pass assetName in the request body");
            }

            streamingPolicyName = streamingPolicyName ?? data?.streamingPolicyName;
            if (streamingPolicyName == null)
            {
                return new OkObjectResult("Please pass streamingPolicyName in the request body");
            }

            contentKeyPolicyName = contentKeyPolicyName ?? data?.contentKeyPolicyName;
            if (contentKeyPolicyName == null)
            {
                return new OkObjectResult("Please pass contentKeyPolicyName in the request body");
            }
            
            ConfigWrapper config = ConfigUtils.GetConfig();

            IAzureMediaServicesClient client;
            try
            {
                client = await Authentication.CreateMediaServicesClientAsync(config);
                log.LogInformation("AMS Client created.");
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

            // Creating a unique suffix so that we don't have name collisions if you run the sample
            // multiple times without cleaning up.
            string uniqueness = Guid.NewGuid().ToString().Substring(0, 13);

            List<StreamingLocatorContentKey> contentKeys = new List<StreamingLocatorContentKey>();

            Guid streamingLocatorId = Guid.NewGuid();
            if (data.StreamingLocatorId != null)
                streamingLocatorId = new Guid((string)(data.streamingLocatorId));
            string streamingLocatorName = "streaminglocator-" + streamingLocatorId.ToString();

            StreamingPolicy streamingPolicy;
            Asset asset;

            try
            {
                asset = await client.Assets.GetAsync(config.ResourceGroup, config.AccountName, assetName);
                log.LogInformation("Asset retrieved.");

            }
            catch (ErrorResponseException ex)
            {
                return new BadRequestObjectResult(ex);
            }

            try
            {
                streamingPolicy = await client.StreamingPolicies.GetAsync(config.ResourceGroup, config.AccountName, streamingPolicyName);
                log.LogInformation("Streaming policy retrieved.");
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when getting streaming policy.");
            }

            if (data.contentKeyPolicyName != null)
            {
                ContentKeyPolicy contentKeyPolicy = null;
                try
                {
                    contentKeyPolicy = await client.ContentKeyPolicies.GetAsync(config.ResourceGroup, config.AccountName, contentKeyPolicyName);
                    log.LogInformation("Content key policy retrieved.");
                }
                catch (ErrorResponseException ex)
                {
                    log.LogInformation(ex.Message);
                    return new BadRequestObjectResult("Error when getting Content Key Policy.");
                }
            }

            if (data.contentKeys != null)
            {
                JsonConverter[] jsonConverters = {
                        new MediaServicesHelperJsonReader()
                    };
                contentKeys = JsonConvert.DeserializeObject<List<StreamingLocatorContentKey>>(data.contentKeys.ToString(), jsonConverters);
            }

            var streamingLocator = new StreamingLocator()
            {
                AssetName = data.assetName,
                StreamingPolicyName = data.streamingPolicyName,
                DefaultContentKeyPolicyName = data.contentKeyPolicyName,
                StreamingLocatorId = streamingLocatorId,
                StartTime = data.startDateTime,
                EndTime = data.endDateTime
            };

            if (contentKeys.Count != 0)
            {
                streamingLocator.ContentKeys = contentKeys;
            }

            streamingLocator.Validate();
            try
            {
                await client.StreamingLocators.CreateAsync(config.ResourceGroup, config.AccountName, streamingLocatorName, streamingLocator);
                log.LogInformation("Streaming locator created.");
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when creating the streaming locator.");
            }

            AnswerBodyModel dataOk = new()
            {
                StreamingLocatorName = streamingLocatorName,
                StreamingLocatorId = streamingLocatorId
            };

            return new OkObjectResult(dataOk);
        }
    }
}
