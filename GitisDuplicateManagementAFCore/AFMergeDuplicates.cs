using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Description;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Newtonsoft.Json;

namespace GitisDuplicateManagementAFCore
{
    public static class AFMergeDuplicates
    {
        [FunctionName("AFMergeDuplicates")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestMessage req, ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            string responseString = null;
            ManualMergeRequest manualMergeRequest = null;

            try
            {
                log.LogInformation($"Request contents are {req?.Content?.ToString()}");
                // Get request body
                manualMergeRequest = await req.Content.ReadAsAsync<ManualMergeRequest>();
            }
            catch (Exception)
            {
                return Helper.CreateHttpResponseMessage(HttpStatusCode.BadRequest, "Invalid Request. Failed to convert string to object");
            }

            try
            {
                IOrganizationService service = CrmConnectionHelper.GetIOrganizationService();
                responseString = await manualMergeRequest.ExecuteAsync(service, log);
                log.LogInformation($"ResponseString {responseString}");
            }
            catch (Exception ex)
            {
                responseString = ex.ToString();
            }

            return Helper.CreateHttpResponseMessage(HttpStatusCode.OK, responseString);
        }
    }
}
