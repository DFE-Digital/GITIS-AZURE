using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Newtonsoft.Json;
//using System.Text.Json;

namespace GitisDuplicateManagementAFCore
{
    public static class TestCrmConnection
    {
        [FunctionName("TestConnect")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get")]HttpRequestMessage req, ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            var organizationService = await Task.Run(() => CrmConnectionHelper.GetIOrganizationService());
            var connectionResult = new { URL = C.APPURL, ConnectionOk = organizationService != null };
            
            var returnObj = new HttpResponseMessage(HttpStatusCode.OK);
            returnObj.Content = new StringContent(JsonConvert.SerializeObject(connectionResult));
            return returnObj;
        }
    }

}
