using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Description;
using System.Threading.Tasks;
using DuplicateManagement;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Newtonsoft.Json;
//using System.Text.Json;

namespace GitisDuplicateManagementAF
{
    public static class TestCrmConnection
    {
        [FunctionName("TestConnect")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get")]HttpRequestMessage req, TraceWriter log)
        {
            log.Info("C# HTTP trigger function processed a request.");
            var organizationService = await Task.Run(() => CrmConnectionHelper.GetIOrganizationService());
            var connectionResult = new { URL = C.APPURL, ConnectionOk = organizationService != null };
            return req.CreateResponse(HttpStatusCode.OK, connectionResult);

        }
    }

}
