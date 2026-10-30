using Azure.Identity;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Azure.Security.KeyVault.Secrets;

namespace GitisDuplicateManagementAFCore
{
    public static class AFDedupeScheduleRun
    {
        static List<string> idList = new List<string>();
        static string csvContent = "id" + Environment.NewLine;

        [FunctionName("DedupeScheduleRun")]
        public static async Task Run([TimerTrigger("0 01 21 * * 1-5")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            try
            {
                
                // Read Key Vault settings from Function App configuration
                string keyVaultUrl =
                Environment.GetEnvironmentVariable("KeyVaultUrl");

                string secretName =
                    Environment.GetEnvironmentVariable("SqlPasswordSecretName");

                if (string.IsNullOrWhiteSpace(keyVaultUrl) ||
                    string.IsNullOrWhiteSpace(secretName))
                {
                    throw new InvalidOperationException(
                        "Key Vault configuration is missing.");
                }

                // Authenticate using the Function App's managed identity
                var secretClient = new SecretClient(
                    new Uri(keyVaultUrl),
                    new DefaultAzureCredential());

                // Retrieve the current password from Azure Key Vault
                KeyVaultSecret secret =
                    (await secretClient.GetSecretAsync(secretName)).Value;

                string sqlPassword = secret.Value;

                SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
                builder.DataSource = "sqlsrv-t1pr-dynamicscrmexports.database.windows.net";
                builder.UserID = "Vgannamani";
                builder.Password = sqlPassword;
                builder.InitialCatalog = "db-t1pr-GITISPRCRMexports";
                builder.Authentication = SqlAuthenticationMethod.SqlPassword;

                string sqlConString = builder.ConnectionString;
                DataTable table = new DataTable();

                SqlConnection sqlCon = new SqlConnection(builder.ConnectionString);

            
                log.LogInformation("Opening SQL Connection...");
                sqlCon.Open();
                log.LogInformation("SQL Connection is now Open");

                string cmdString = "SELECT * FROM  PowerBI.Registration_Merge_Duplicates_RawData_Dedupe";
                using (SqlCommand cmd = new SqlCommand(cmdString, sqlCon))
                {
                    table.Load(cmd.ExecuteReader());

                    if (table == null || table.Rows.Count == 0) return;

                    foreach (DataRow dr in table.Rows)
                    {
                        string id = dr["id"].ToString();
                        if (id.Length != 36) continue;

                        idList.Add(id);
                        csvContent += id + Environment.NewLine;
                    }
                }
                if (sqlCon.State == ConnectionState.Open) sqlCon.Close();

                await CreateTriggerFileAsync();
            }
            catch (Exception ex)
            {

                log.LogError(ex.ToString());
            }
        }
        private static async Task CreateTriggerFileAsync()
        {

            CloudBlobContainer targetContainer;

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            Microsoft.WindowsAzure.Storage.CloudStorageAccount storageAccount = Microsoft.WindowsAzure.Storage.CloudStorageAccount.Parse(storageConnectionString);

            CloudBlobClient blobclient = storageAccount.CreateCloudBlobClient();
            targetContainer = blobclient.GetContainerReference("gitis-dupman-trigger");
            await targetContainer.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new Microsoft.WindowsAzure.Storage.OperationContext());
            await targetContainer.CreateIfNotExistsAsync();

            CloudBlockBlob targetBlob;
            targetBlob = targetContainer.GetBlockBlobReference("DuplicatesFromSQLView.csv");
            targetBlob.Properties.ContentType = "text/plain";


            byte[] byteArray = Encoding.ASCII.GetBytes(csvContent);
            MemoryStream stream = new MemoryStream(byteArray);
            await targetBlob.UploadFromStreamAsync(stream);
        }
    }
}
