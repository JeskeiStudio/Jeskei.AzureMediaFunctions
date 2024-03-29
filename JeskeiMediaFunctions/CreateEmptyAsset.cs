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
using Microsoft.WindowsAzure.Storage.Blob;
using System.Linq;

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
            /// Asset name
            /// </summary>
            [JsonProperty("assetName")]
            public string AssetName { get; set; }

            /// <summary>
            /// Asset description
            /// Optional.
            /// </summary>
            [JsonProperty("assetDescription")]
            public string AssetDescription { get; set; }
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

            /// <summary>
            /// Uri for uploading blob
            /// </summary>
            [JsonProperty("sasUri")]
            public Uri SasUri { get; set; }
        }

        [FunctionName("CreateEmptyAsset")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            if (data?.assetOwnerAddress == null || data?.assetOwnerAddress == "0x0000000000000000000000000000000000000000")
            {
                return new OkObjectResult("Please pass assetOwnerAddress in the request body");
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

            string assetName = $"{data.assetOwnerAddress}-{uniqueness}_{data.assetName}";
            
            Asset asset;
            
            try
            {
                // let's create the asset
                asset = await AssetUtils.CreateAssetAsync(client, log, config.ResourceGroup, config.AccountName, assetName);

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

            var response = client.Assets.ListContainerSas(config.ResourceGroup,
                                                          config.AccountName,
                                                          assetName,
                                                          permissions: AssetContainerPermission.ReadWrite,
                                                          expiryTime: DateTime.UtcNow.AddHours(24).ToUniversalTime());

            var sasUri = new Uri(response.AssetContainerSasUrls.First());


            AnswerBodyModel dataOk = new()
            {
                AssetName = asset.Name,
                AssetId = asset.AssetId,
                Container = asset.Container,
                SasUri = sasUri
            };

            return new OkObjectResult(dataOk);
        }
    }
}
