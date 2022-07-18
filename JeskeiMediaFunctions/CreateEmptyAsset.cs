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

namespace JeskeiMediaFunctions
{
    public static class CreateEmptyAsset
    {
        /// <summary>
        /// Data to pass as an input to the function
        /// </summary>
        private class RequestBodyModel
        {
            /// <summary>
            /// Wallet address of wallet uploading asset
            /// Mandatory.
            /// </summary>
            [JsonProperty("assetOwnerAddress")]
            public string AssetOwnerAddress { get; set; }

            /// <summary>
            /// Signature of wallet owner to prove ownership of asset being loaded
            /// Mandatory.
            /// </summary>
            [JsonProperty("assetOwnerSignature")]
            public string AssetOwnerSignature { get; set; }

            /// <summary>
            /// Asset description
            /// Optional.
            /// </summary>
            [JsonProperty("assetDescription")]
            public string AssetDescription { get; set; }

            /// <summary>
            /// Name of the attached storage account to use for the asset.
            /// Optional.
            /// </summary>
            [JsonProperty("assetStorageAccount")]
            public string AssetStorageAccount { get; set; }
        }

        /// <summary>
        /// Data output by the function
        /// </summary>
        private class AnswerBodyModel
        {
            /// <summary>
            /// Name of the asset created.
            /// </summary>
            [JsonProperty("assetName")]
            public string AssetName { get; set; }

            /// <summary>
            /// Id of the asset.
            /// </summary>
            [JsonProperty("assetId")]
            public Guid AssetId { get; set; }

            /// <summary>
            /// Name of the storage container.
            /// </summary>
            [JsonProperty("container")]
            public string Container { get; set; }
        }


        [FunctionName("CreateEmptyAsset")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string assetOwnerAddress = req.Query["assetOwnerAddress"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            assetOwnerAddress = assetOwnerAddress ?? data?.assetOwnerAddress;
            /*
            if (assetOwnerAddress == null)
            {
                return new OkObjectResult("Please pass assetOwnerAddress in the request body");
            }
            */
            ConfigWrapper config = ConfigUtils.GetConfig();
            return new OkObjectResult(config.ToString());

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
            string assetName = $"{data.assetOwnerAddress}-{uniqueness}";

            Asset asset;

            try
            {
                // let's create the asset
                asset = await AssetUtils.CreateAssetAsync(client, log, config.ResourceGroup, config.AccountName, assetName, data.assetStorageAccount, data.assetDescription);
                log.LogInformation($"Asset '{assetName}' created.");
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when creating the asset.");
            }

            try
            {
                // let's get the asset to have full metadata like container
                asset = await client.Assets.GetAsync(config.ResourceGroup, config.AccountName, assetName);
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when getting the created asset.");
            }

            AnswerBodyModel dataOk = new()
            {
                AssetName = asset.Name,
                AssetId = asset.AssetId,
                Container = asset.Container
            };

            return new OkObjectResult(dataOk);
        }
    }
}
