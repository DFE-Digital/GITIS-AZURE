using System;
using System.Net;
using System.ServiceModel.Description;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace GitisDuplicateManagementAFCore
{
    public static class AFProcessDuplicate
    {
        [FunctionName("ProcessDuplicate")]
        public static async Task Run([QueueTrigger("duplicate-contacts", Connection = "AzureStorageQueue")] string myQueueItem, ILogger log)
        {
            DMLog dmLog = new DMLog();
            dmLog.ExecutionTime = DateTime.Now;

            MergeConfig mergeConfig = new MergeConfig();

            if (myQueueItem.Length != 36) return;
            Guid recordId = new Guid(myQueueItem);

            try
            {
                #region business logic

                if (Environment.GetEnvironmentVariable("D365_LOG_SNAPSHOT") == "YES") mergeConfig.LogSnapshot = true;
                else mergeConfig.LogSnapshot = false;

                if (Environment.GetEnvironmentVariable("TestRun") == "YES") mergeConfig.TestRun = true;
                else mergeConfig.TestRun = false;

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
                    return;
                }


                #region RetrieiveDuplicates

                DMRetrieveRequest dmRetrieveRequest = new DMRetrieveRequest();
                DMRetrieveResponse dmRetrieveResponse = await dmRetrieveRequest.SendAsync(service, log, recordId, dmLog, mergeConfig);

                if (dmRetrieveResponse == null) return;

                #endregion

                #region Merge


                DMMergeRequest dmMergeRequest = new DMMergeRequest(dmRetrieveResponse, dmLog, mergeConfig);

                if (mergeConfig.TestRun == true)
                {
                    try
                    {
                        dmMergeRequest.LogPreMerge(service);
                    }
                    catch (Exception ex)
                    {

                        dmLog.ExecutionStatus = MergeStatus.FAIL;
                        dmLog.ResponseText = "Pre Execution Log FAILED. ";
                        if (ex.ToString().Length > 450) dmLog.ResponseText += ex.ToString().Substring(0, 450);
                        else dmLog.ResponseText += ex.ToString();
                    }
                    finally
                    {
                        await dmLog.WritePreExecutionLogAsync();
                    }
                    return;
                }

                await dmMergeRequest.ExecuteAsync(service, log);
                if (dmMergeRequest.MergeType == MergeOption.SKIP_DuplicateIsSubscriber)
                {
                    dmLog.LoggedBy = LoggedByFunction.AFProcessDuplicate;
                    return;
                }
                else if (dmMergeRequest.MergeType == MergeOption.Skip_MasterRecordClosed ||
                    dmMergeRequest.MergeType == MergeOption.Skip_MultipleOwners)
                {

                    if (dmMergeRequest.MergeType == MergeOption.Skip_MultipleOwners)
                    {
                        dmLog.PublishMessage("dupman-multipleownersfortpukreview", dmLog.RecordId);
                    }

                    dmLog.LoggedBy = LoggedByFunction.AFProcessDuplicate;
                    return;
                }
                else if (dmMergeRequest.MergeType == MergeOption.SKIP_MultipleOwners_Notified)
                {
                    dmLog.LoggedBy = LoggedByFunction.AFProcessDuplicate;
                    return;
                }
                else if (dmLog.ExecutionStatus == MergeStatus.SUCCESS && mergeConfig.LogSnapshot)
                {
                    Entity mergedEntity = service.Retrieve(dmRetrieveResponse.MasterEntity.LogicalName, dmRetrieveResponse.MasterEntity.Id, new ColumnSet(true));
                    dmRetrieveResponse.MergedDMEntity = new DMEntity(mergedEntity, mergeConfig);
                    dmRetrieveResponse.MergedDMEntity.GetRelatedEntities(service);
                    dmRetrieveResponse.MergedDMEntity.GetMarketingEvents(service);
                    await dmRetrieveResponse.CreateSnapshotHtmlAsync();
                }

                dmLog.LoggedBy = LoggedByFunction.AFProcessDuplicate;
                log.LogInformation($"C# Queue trigger function processed: {myQueueItem}");
                return;

                #endregion

                #endregion business logc
            }
            catch (Exception ex)
            {
                if (ex.ToString().Length > 450) dmLog.ResponseText += ex.ToString().Substring(0, 450);
                else dmLog.ResponseText += ex.ToString();
            }
            finally
            {
                if (mergeConfig.TestRun == true) await dmLog.WritePreExecutionLogAsync();
                else await dmLog.WriteExecutionLogAsync();
            }



        }
    }
}
