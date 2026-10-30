using Microsoft.Azure.WebJobs.Host;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.IO;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.Extensions.Logging;

namespace GitisDuplicateManagementAFCore
{
    public class DMLog
    {
        public ILogger traceWriter { get; set; }
        public int NoOfDuplicates { get; set; }
        public string RecordId = "";
        public bool IsMaster { get; set; }
        public string MasterId = "";
        public DateTime ExecutionTime { get; set; }
        public string ResponseText = "";
        public MergeStatus ExecutionStatus { get; set; }
        public LoggedByFunction LoggedBy { get; set; }
        public string MostRecentRecordId { get; set; }

        public string RecordURL { get; set; }
        public string MasterOwner { get; set; }
        public string MasterOwnerType { get; set; }
        public bool MultipleOwner { get; set; }
        public bool MasterRecordClosed { get; set; }
        public bool MasterHasITT { get; set; }
        public bool DuplicateHasITT { get; set; }
        public bool MasterHasReleaseDate { get; set; }
        public bool DuplicateHasReleaseDate { get; set; }
        public bool SESOverwrite { get; set; }
        public bool DoNotOverwriteMaster { get; set; }
        public bool IsMasterSubscriber { get; set; }
        public bool IsDuplicateSubscriber { get; set; }
        public bool IsMasterLatest { get; set; }
        public bool HasDuplicateOwnedByAdviser { get; set; }
        public string DuplicateCandicateAdviserStatus { get; set; }

        public void Trace(string text)
        {
            traceWriter.LogInformation(text);
        }
        public async Task WriteExecutionLogAsync()
        {
            ExecutionTime = DateTime.Now;
            int tryCount = 0;
            RecordId = RecordId.ToUpper();

            try
            {
                tryCount++;

                CloudBlobClient client;
                CloudBlobContainer container;

                string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
                client = storageAccount.CreateCloudBlobClient();
                container = client.GetContainerReference("gitis-dupman-logs");
                await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

                string bloblName;
                CloudAppendBlob blob;

                bloblName = "DupManLog_" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
                blob = container.GetAppendBlobReference(bloblName);

                string logText = "";
                bool blobExists = await blob.ExistsAsync();

                if (ResponseText.Length > 500) ResponseText = ResponseText.Substring(0, 500);

                if (blobExists) //appent to existing blob
                {
                    logText += Environment.NewLine;
                    logText += RecordId + "," + RecordURL + "," + IsMaster + "," + MasterId + "," + NoOfDuplicates + "," + ExecutionStatus.ToString() + "," + ExecutionTime.ToString("dd-MM-yyyy HH:mm:ss") + "," +
                        GetExecutionDuration() + ",\"" + ResponseText + "\"" + "," + LoggedBy.ToString(); ;
                    byte[] byteArray = Encoding.ASCII.GetBytes(logText);
                    MemoryStream stream = new MemoryStream(byteArray);

                TryToWriteAgain: //this is to ensure it writes to the log in case it fails due to concurrancy

                    try
                    {
                        blob = container.GetAppendBlobReference(bloblName);
                        await blob.AppendFromStreamAsync(stream);
                    }
                    catch (Exception ex)
                    {

                        string exString = ex.ToString();
                        if (tryCount < 5)
                        {
                            await Task.Run(() => System.Threading.Thread.Sleep(2000));
                            goto TryToWriteAgain;
                        }
                    }
                }
                else //create new blob
                {
                    logText = "RecordId,RecordURL,IsMaster,MasterId,NoOfDuplicates, ExecutionStatus,ExecutionTime,ExecutionDuration,ResponseText,LoggedByFunction";
                    logText += Environment.NewLine;
                    logText += RecordId + "," + RecordURL + "," + IsMaster + "," + MasterId + "," + NoOfDuplicates + "," + ExecutionStatus.ToString() + "," + ExecutionTime.ToString("dd-MM-yyyy HH:mm:ss") + "," +
                        GetExecutionDuration() + "," + ResponseText + "," + LoggedBy.ToString();

                    byte[] byteArray = Encoding.ASCII.GetBytes(logText);
                    MemoryStream stream = new MemoryStream(byteArray);
                    await blob.UploadFromStreamAsync(stream);
                }
            }
            catch (Exception)
            {

            }
        }

        public async Task WritePreExecutionLogAsync()
        {
            ExecutionTime = DateTime.Now;
            int tryCount = 0;
            RecordId = RecordId.ToUpper();

            try
            {
                tryCount++;

                CloudBlobClient client;
                CloudBlobContainer container;

                string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
                client = storageAccount.CreateCloudBlobClient();
                container = client.GetContainerReference("gitis-dupman-preexecutionlog");
                await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

                string bloblName;
                CloudAppendBlob blob;

                bloblName = "DupManPreExecutionLog_" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
                blob = container.GetAppendBlobReference(bloblName);

                string logText = "";
                bool blobExists = await blob.ExistsAsync();

                if (ResponseText.Length > 500) ResponseText = ResponseText.Substring(0, 500);

                if (blobExists) //appent to existing blob
                {
                    logText += Environment.NewLine;
                    logText += RecordId + "," + RecordURL + "," + IsMaster + "," + MasterId + "," + MasterOwner + "," + MasterOwnerType + "," + MultipleOwner +
                                       "," + MasterRecordClosed + "," + IsMasterLatest + "," + HasDuplicateOwnedByAdviser + "," + DuplicateCandicateAdviserStatus + "," + MasterHasITT + "," + DuplicateHasITT + "," + MasterHasReleaseDate + ","
                                       + DuplicateHasReleaseDate + "," + NoOfDuplicates + "," + ExecutionStatus.ToString() + "," + ExecutionTime.ToString("dd-MM-yyyy HH:mm:ss") + "," +
                                       SESOverwrite + "," + DoNotOverwriteMaster + "," + ResponseText;

                    byte[] byteArray = Encoding.ASCII.GetBytes(logText);
                    MemoryStream stream = new MemoryStream(byteArray);

                TryToWriteAgain: //this is to ensure it writes to the log in case it fails due to concurrancy

                    try
                    {
                        blob = container.GetAppendBlobReference(bloblName);
                        await blob.AppendFromStreamAsync(stream);
                    }
                    catch (Exception ex)
                    {

                        string exString = ex.ToString();
                        if (tryCount < 5)
                        {
                            System.Threading.Thread.Sleep(2000);
                            goto TryToWriteAgain;
                        }
                    }


                }
                else //create new blob
                {
                    logText = "RecordId,RecordURL, IsMaster,MasterId,MasterOwner,MasterOwnerType,HasMultipleOwner,MasterRecordClosed,IsMasterLatest," +
                        "HasDuplicateOwnedByAdviser,DuplicateCandidateAdviserStatus,MasterHasITTProviderAppliedTo, DuplicateHasITTProviderAppliedTo,MasterHasReleaseDate,DuplicateHasReleaseDate," +
                        "NoOfDuplicates, ExecutionStatus,ExecutionTime,SESOverwrite,DoNotOverwriteMaster,ResponseText";
                    logText += Environment.NewLine;
                    logText += RecordId + "," + RecordURL + "," + IsMaster + "," + MasterId + "," + MasterOwner + "," + MasterOwnerType + "," + MultipleOwner +
                                       "," + MasterRecordClosed + "," + IsMasterLatest + "," + HasDuplicateOwnedByAdviser + "," + DuplicateCandicateAdviserStatus + "," + MasterHasITT + "," + DuplicateHasITT + "," + MasterHasReleaseDate + ","
                                       + DuplicateHasReleaseDate + "," + NoOfDuplicates + "," + ExecutionStatus.ToString() + "," + ExecutionTime.ToString("dd-MM-yyyy HH:mm:ss") + "," +
                                       SESOverwrite + "," + DoNotOverwriteMaster + "," + ResponseText;


                    byte[] byteArray = Encoding.ASCII.GetBytes(logText);
                    MemoryStream stream = new MemoryStream(byteArray);
                    await blob.UploadFromStreamAsync(stream);
                }
            }
            catch (Exception)
            {

            }
        }
        private int GetExecutionDuration()
        {
            double totalTime = (DateTime.Now - ExecutionTime).TotalSeconds;
            return Convert.ToInt32(totalTime);
        }
        public async void PublishMessage(string queueName, string message)
        {

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            CloudQueueClient queueClient = storageAccount.CreateCloudQueueClient();
            CloudQueue queue = queueClient.GetQueueReference(queueName);

            await queue.CreateIfNotExistsAsync();

            await SendMessageAsync(queue, message);
        }
        private static async Task SendMessageAsync(CloudQueue theQueue, string newMessage)
        {
            bool createdQueue = await theQueue.CreateIfNotExistsAsync();
            CloudQueueMessage message = new CloudQueueMessage(newMessage);
            await theQueue.AddMessageAsync(message);
        }

    }

    public enum LoggedByFunction
    {
        AFProcessSourceFile,
        AFProcessDuplicate
    }
}
