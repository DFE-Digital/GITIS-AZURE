using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.ServiceModel.Description;
using System.Net;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage;

namespace GitisDuplicateManagementAFCore
{
    public static class AFProcessSourceFile
    {
        static List<string> publishedIDs = new List<string>();

        [FunctionName("ProcessSourceFile")]
        public static async Task Run([BlobTrigger("gitis-dupman-trigger/{name}", Connection = "AzureWebJobsStorage")]Stream myBlob, string name, ILogger log)
        {
            publishedIDs.Clear();
            DateTime startTime = DateTime.Now;
            log.LogInformation($"C# Blob trigger function Processed blob\n Name:{name} \n Size: {myBlob.Length} Bytes");

            List<string> sourceIds = new List<string>();
            List<string> remainingIds = new List<string>();

            string idListRemaining = "";
            string text = "";
            List<string> guids = new List<string>();

            int tryCount = 1;

            bool testRun = true;

            if (Environment.GetEnvironmentVariable("TestRun") == "YES") testRun = true;
            else testRun = false;

            #region ReadSource

            using (StreamReader reader = new StreamReader(myBlob))
            {
                text = reader.ReadToEnd();

                if (!name.Contains("R_"))
                {
                    string targetFileName = "PROCESSING_" + name;
                    await CopySourceFileAsync(name, text, targetFileName);
                }

                text = text.ToUpper();
                if (text.Length < 36) return;

                char[] delims = new[] { '\r', '\n' };
                guids = text.Split(delims, StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            sourceIds = guids.Distinct().ToList();
            remainingIds = guids.Distinct().ToList();
            log.LogInformation($"Source ID Count:{sourceIds.Count().ToString()}");
        #endregion

        TryToGetOrgServiceAgain:

            tryCount++;

            if (tryCount > 5)
            {
                log.LogInformation($"ERROR! Unable to retrieve OrganisationService. Try Count Exceeded 5");
                return;
            }

            IOrganizationService service = CrmConnectionHelper.GetIOrganizationService();
            if (service == null)
            {
                await Task.Run(() => System.Threading.Thread.Sleep(1000 * 15));
                goto TryToGetOrgServiceAgain;
            }

            foreach (string item in sourceIds)
            {
                if(!testRun) await Task.Run(() => System.Threading.Thread.Sleep(5000)); //Avoid Throttling

                if (!remainingIds.Contains(item)) continue;
                remainingIds.RemoveAll(r => r.ToUpper() == item.ToUpper());

                if (item.Length != 36) continue;

                DMLog dmLog = new DMLog();
                dmLog.ExecutionTime = DateTime.Now;
                dmLog.traceWriter = log;
                dmLog.RecordId = item;

                Entity entity = null;
                string entityid = "";

                try
                {
                    #region Get Record

                    entity = service.Retrieve("contact", new Guid(item), new ColumnSet("statecode"));
                    entityid = entity.Id.ToString().ToUpper();

                    int stateCode = ((OptionSetValue)entity["statecode"]).Value;

                    if (stateCode == 1) //Inactive
                    {
                        dmLog.ExecutionStatus = MergeStatus.SKIP_InactiveRecord;
                        dmLog.ResponseText = "Rcord is INACTIVE";
                        remainingIds.RemoveAll(r => r.ToUpper() == item.ToUpper());

                        dmLog.LoggedBy = LoggedByFunction.AFProcessSourceFile;
                        await dmLog.WriteExecutionLogAsync();
                        continue;
                    }

                    #endregion Get Record

                    #region Get Duplicates

                    Guid recordId = new Guid(item);

                    RetrieveDuplicatesRequest dupRequest = new RetrieveDuplicatesRequest();

                    Entity sourceEntity = new Entity("contact", recordId);
                    dupRequest.BusinessEntity = sourceEntity;
                    dupRequest.MatchingEntityName = sourceEntity.LogicalName;
                    dupRequest.PagingInfo = new PagingInfo() { PageNumber = 1, Count = 50 };

                    RetrieveDuplicatesResponse retrieveDuplicateResponse = (RetrieveDuplicatesResponse)service.Execute(dupRequest);

                    if (retrieveDuplicateResponse == null || retrieveDuplicateResponse.DuplicateCollection.Entities.Count == 0)
                    {
                        dmLog.ExecutionStatus = MergeStatus.SKIP_NoDuplicates;
                        dmLog.ResponseText = "No Duplicates Found";
                        remainingIds.RemoveAll(r => r.ToUpper() == item.ToUpper());
                        dmLog.LoggedBy = LoggedByFunction.AFProcessSourceFile;
                        await dmLog.WriteExecutionLogAsync();
                        continue;
                    }
                    else
                    {
                        bool hasDuplicate = false;

                        foreach (Entity dupEntity in retrieveDuplicateResponse.DuplicateCollection.Entities)
                        {
                            if(testRun == false) remainingIds.RemoveAll(r => r.ToUpper() == dupEntity.Id.ToString().ToUpper());

                            int dupStateCode = ((OptionSetValue)dupEntity["statecode"]).Value;
                            if (hasDuplicate == false)
                            {
                                if (dupStateCode == 0) //Active
                                {
                                    hasDuplicate = true;
                                }
                            }
                        }

                        if (!hasDuplicate)
                        {
                            dmLog.ExecutionStatus = MergeStatus.SKIP_NoDuplicates;
                            dmLog.ResponseText = "No Duplicates Found";
                            dmLog.LoggedBy = LoggedByFunction.AFProcessSourceFile;

                            //      await dmLog.WriteExecutionLogAsync();
                            continue;
                        }
                        else
                        {
                            if (publishedIDs.Contains(item.ToUpper())) continue;

                            await WriteGuidToStorageQueueAsync("duplicate-contacts", item.ToUpper(), log);
                            publishedIDs.Add(item.ToUpper());
                        }
                    }

                    int totalTime = (int)(DateTime.Now - startTime).TotalSeconds / 60;
                    //   log.LogInformation($"Time Elapsed:{totalTime} | Remaining: {remainingIds.Count.ToString()}");

                    if (totalTime > 8)
                    {
                        // stop this function and retrigger to avoid 10 minute timeout
                        break;
                    }

                    #endregion Get Duplicates
                }
                catch (Exception ex)
                {
                    dmLog.ExecutionStatus = MergeStatus.FAIL;
                    dmLog.ResponseText = ex.Message.ToString();
                    dmLog.LoggedBy = LoggedByFunction.AFProcessSourceFile;
                    await dmLog.WriteExecutionLogAsync();
                    continue;
                }
            }

            if (remainingIds.Count > 0)
            {
                foreach (string item in remainingIds)
                {
                    idListRemaining += item + Environment.NewLine;
                }
                
                await RecreateSourceFileAsync("R_" + name, idListRemaining);

            }
            else
            {
                string processingBlobName = name.Replace("R_", "");
                processingBlobName = "PROCESSING_" + processingBlobName;
                await RenameProcessingFileAsync(processingBlobName);

            }

            await DeleteSourceFileAsync(name);

        }

        private static async Task DeleteSourceFileAsync(string sourceBlobName)
        {
            CloudBlobContainer sourceContainer;
            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            Microsoft.WindowsAzure.Storage.CloudStorageAccount storageAccount = Microsoft.WindowsAzure.Storage.CloudStorageAccount.Parse(storageConnectionString);

            CloudBlobClient blobclient = storageAccount.CreateCloudBlobClient();
            sourceContainer = blobclient.GetContainerReference("gitis-dupman-trigger");

            var sourceBlob = sourceContainer.GetBlockBlobReference(sourceBlobName);

            if (await sourceBlob.ExistsAsync()) await sourceBlob.DeleteAsync();
        }

        private static async Task CopySourceFileAsync(string sourceBlobName, string sourceContent, string targetBlobName)
        {
            CloudBlobContainer sourceContainer;
            CloudBlobContainer targetContainer;

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            Microsoft.WindowsAzure.Storage.CloudStorageAccount storageAccount = Microsoft.WindowsAzure.Storage.CloudStorageAccount.Parse(storageConnectionString);

            CloudBlobClient blobclient = storageAccount.CreateCloudBlobClient();
            sourceContainer = blobclient.GetContainerReference("gitis-dupman-trigger");
            targetContainer = blobclient.GetContainerReference("gitis-dupman-processed");
            await targetContainer.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new Microsoft.WindowsAzure.Storage.OperationContext());

            await targetContainer.CreateIfNotExistsAsync();
            CloudBlockBlob targetBlob;
            targetBlob = targetContainer.GetBlockBlobReference(targetBlobName);
            targetBlob.Properties.ContentType = "text/plain";


            byte[] byteArray = Encoding.ASCII.GetBytes(sourceContent);
            MemoryStream stream = new MemoryStream(byteArray);

            await targetBlob.UploadFromStreamAsync(stream);
        }

        private static async Task WriteGuidToStorageQueueAsync(string queueName, string dupId, ILogger log)
        {
            if (dupId.Length != 36) return;

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            CloudQueueClient queueClient = storageAccount.CreateCloudQueueClient();
            //  CloudQueue queue = queueClient.GetQueueReference("duplicate-contacts");
            CloudQueue queue = queueClient.GetQueueReference(queueName);

            bool createdQueue = await queue.CreateIfNotExistsAsync();
            CloudQueueMessage message = new CloudQueueMessage(dupId);
            await queue.AddMessageAsync(message);


            //  SendMessageAsync(queue, dupId, log);
        }

        static async Task SendMessageAsync(CloudQueue theQueue, string newMessage, ILogger log)
        {
            bool createdQueue = await theQueue.CreateIfNotExistsAsync();

            CloudQueueMessage message = new CloudQueueMessage(newMessage);
            await theQueue.AddMessageAsync(message);
            
           // ???
            await theQueue.AddMessageAsync(message);
        }

        private static async Task RecreateSourceFileAsync(string sourceBlobName, string content)
        {
            CloudBlobContainer container;
            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            Microsoft.WindowsAzure.Storage.CloudStorageAccount storageAccount = Microsoft.WindowsAzure.Storage.CloudStorageAccount.Parse(storageConnectionString);

            CloudBlobClient blobclient = storageAccount.CreateCloudBlobClient();
            container = blobclient.GetContainerReference("gitis-dupman-trigger");

            CloudBlockBlob targetBlob;
            targetBlob = container.GetBlockBlobReference(sourceBlobName);
            targetBlob.Properties.ContentType = "text/plain";

            byte[] byteArray = Encoding.ASCII.GetBytes(content);
            MemoryStream stream = new MemoryStream(byteArray);

            await targetBlob.UploadFromStreamAsync(stream);
        }

        private static async Task RenameProcessingFileAsync(string processingBlobName)
        {
            CloudBlobContainer container;

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            Microsoft.WindowsAzure.Storage.CloudStorageAccount storageAccount = Microsoft.WindowsAzure.Storage.CloudStorageAccount.Parse(storageConnectionString);

            CloudBlobClient blobclient = storageAccount.CreateCloudBlobClient();
            container = blobclient.GetContainerReference("gitis-dupman-processed");

            if ( !await container.ExistsAsync()) return;

            CloudBlockBlob sourceBlob;
            sourceBlob = container.GetBlockBlobReference(processingBlobName);
            if (! await sourceBlob.ExistsAsync()) return;
            string sourceContent = await sourceBlob.DownloadTextAsync();


            CloudBlockBlob targetBlob;
            processingBlobName = processingBlobName.Replace("PROCESSING_", "");
            string targetBloblName = DateTime.Now.ToString("yyyyMMdd_HHmm") + "_PROCESSED_" + processingBlobName;
            targetBlob = container.GetBlockBlobReference(targetBloblName);
            targetBlob.Properties.ContentType = "text/plain";

            byte[] byteArray = Encoding.ASCII.GetBytes(sourceContent);
            MemoryStream stream = new MemoryStream(byteArray);

            await targetBlob.UploadFromStreamAsync(stream);
            await sourceBlob.DeleteAsync();
        }

    }

}
