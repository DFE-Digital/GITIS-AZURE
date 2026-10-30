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
//using System.Text.Json;

namespace GitisDuplicateManagementAFCore
{
    public static class AFRetrieveDuplicates
    {
        [FunctionName("AFRetrieveDuplicates")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequestMessage req, ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            string dupResponseJsonString = "";
            DMRetrieveResponse dmRetrieveResponse = null;
            ManualRetrieveResponse manualMergeResponse = null;

            // parse query parameter
            var qs = req.RequestUri.ParseQueryString();

            string recordId = qs.Get("recordid");
            var duplicateIds = qs.Get("duplicateids");

            if (recordId == null)
            {
                log.LogWarning("RecordId is Null.. Checking body for record Id");
                // Get request body
                dynamic data = await req.Content.ReadAsAsync<object>();
                recordId = data?.name;
            }

            if (recordId == null) return Helper. CreateHttpResponseMessage(HttpStatusCode.BadRequest, "Record ID cannot be null");
            else if (recordId.Length != 36) return Helper.CreateHttpResponseMessage(HttpStatusCode.BadRequest, "Invalid Record ID");
            else
            {
                log.LogInformation("Calling Get DuplicatesAsync");
                dmRetrieveResponse = await GetDuplicatesAsync(recordId, duplicateIds, log);

                if (dmRetrieveResponse != null)
                {
                    manualMergeResponse = dmRetrieveResponse.ConvertToManualMergeResponse();
                    log.LogInformation(dmRetrieveResponse.ResponseText);
                    dupResponseJsonString = JsonConvert.SerializeObject(manualMergeResponse);
                    log.LogInformation(dupResponseJsonString);
                }
            }

            return Helper.CreateHttpResponseMessage(HttpStatusCode.OK, manualMergeResponse);
        }

        private static async Task<DMRetrieveResponse> GetDuplicatesAsync(string RecordId,string duplicateIds, ILogger log)
        {
            log.LogInformation("Entering: GetDuplicatesAsync(string RecordId {0},string duplicateIds {1}, ILogger log)", RecordId, duplicateIds);
            DMRetrieveResponse dmRetrieveResponse = null;
            DMLog dmLog = new DMLog();
            dmLog.ExecutionTime = DateTime.Now;

            MergeConfig mergeConfig = new MergeConfig();
            if (RecordId.Length != 36) return null;

            Guid recordId = new Guid(RecordId);

            Guid[] otherRecords = null;

            dmLog.traceWriter = log;
            dmLog.Trace($"STARTED: C# Timer trigger function at: {DateTime.Now}");
            dmLog.RecordId = recordId.ToString();

            IOrganizationService service = CrmConnectionHelper.GetIOrganizationService();

            if (service == null)
            {
                dmLog.ResponseText = "Unable to connect to Dynamics";
                dmLog.ExecutionStatus = MergeStatus.SKIP_ErrorOccured;
                dmLog.LoggedBy = LoggedByFunction.AFProcessDuplicate;
                await dmLog.WriteExecutionLogAsync();
                return null;
            }

            DMRetrieveRequest dmRetrieveRequest = new DMRetrieveRequest();
            dmRetrieveRequest.ManualMerge = true;

            if (!string.IsNullOrEmpty(duplicateIds))
            {
                dmLog.Trace("We have other ids so returning an array of these Guids");
                string cleanString = Uri.UnescapeDataString(duplicateIds);
                string[] idsAsString = cleanString.Split(",");
                otherRecords = idsAsString
                                .Where(x => x?.ToLower().CompareTo(RecordId.ToLower()) != 0 && Guid.TryParse(x, out Guid GuidTemp))?
                                .Select(x => new Guid(x)).ToArray();

                foreach (var guid in otherRecords)
                {
                    log.LogInformation("Other Records ID {0}", guid);
                }
            }
            else
            {
                log.LogInformation("We don't have other ids so proceeding as normal");
            }

            dmRetrieveResponse = await dmRetrieveRequest.SendAsync(service, log, recordId, dmLog, mergeConfig, otherRecords);

            if (dmRetrieveResponse != null && dmRetrieveResponse.DuplicateEntities != null && dmRetrieveResponse.DuplicateEntities.Count > 0)
            {
                dmRetrieveResponse.ResponseText = dmRetrieveResponse.DuplicateEntities.Count + " Duplicates Found";
            }
            else if (dmRetrieveResponse != null)
            {
                dmRetrieveResponse.ResponseText = "No Duplicates Found";
            }

            return dmRetrieveResponse;

        }

    }

}
