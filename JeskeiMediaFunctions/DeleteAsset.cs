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
    public static class DeleteAsset
    {
        /// <summary>
        /// Data to pass as an input to the function
        /// </summary>
        private class RequestBodyModel
        {
            /// <summary>
            /// Name of the asset to delete.
            /// Mandatory.
            /// </summary>
            [JsonProperty("assetName")]
            public string AssetName { get; set; }

            /// <summary>
            /// Signature of wallet owner to prove ownership of asset being deleted
            /// Mandatory.
            /// </summary>
            [JsonProperty("assetOwnerSignature")]
            public string AssetOwnerSignature { get; set; }
        }

        /// <summary>
        /// Function which deletes an asset.
        /// </summary>
        /// <param name="req"></param>
        /// <param name="executionContext"></param>
        /// <returns></returns>
        [FunctionName("DeleteAsset")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string assetName = req.Query["assetName"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            assetName = assetName ?? data?.assetName;

            if (assetName == null)
            {
                return new OkObjectResult("Please pass asset name in the request body");
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
                return new BadRequestObjectResult(e);
            }

            // Set the polling interval for long running operations to 2 seconds.
            // The default value is 30 seconds for the .NET client SDK
            client.LongRunningOperationRetryTimeout = 2;

            try
            {
                // let's delete the asset
                await client.Assets.DeleteAsync(config.ResourceGroup, config.AccountName, assetName);
                log.LogInformation($"Asset '{assetName}' deleted.");
            }
            catch (ErrorResponseException ex)
            {
                log.LogInformation(ex.Message);
                return new BadRequestObjectResult("Error when deleting the asset.");
            }

            return new OkObjectResult(null);
        }
    }
}
