using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{
    public class MergeConfig
    {
        public List<DMAttribute> Attributes = new List<DMAttribute>();
        public List<DMAttribute> RequiredApplyAttributes = new List<DMAttribute>();
        public List<RelatedEntity> RelatedEntities = new List<RelatedEntity>();
        public List<ExtraFieldHandling> ExtraFieldHandlingList = new List<ExtraFieldHandling>();

        public List<string> RequiredApplyAttributesList = new List<string>();
        public List<string> RequiredApplyChildrenList = new List<string>();
        public List<string> AttributeList = new List<string>();
        public List<Guid> Advisors = new List<Guid>();
        public List<string> MultOwnersExList = new List<string>();

        public string CrmUrl { get; private set; }
        public bool LogSnapshot { get; set; }
        public bool TestRun { get; set; }
        public void GetData(IOrganizationService service, ILogger log)
        {

            log.LogInformation("Entering: MergeConfig.GetData");
            Attributes.Clear();
            RelatedEntities.Clear();
            AttributeList.Clear();

            string attributeList = "";
            string relatedEntityList = "";
            string requiredApplyFields = "";
            string extrafieldHanlding = "";
            string relatedApplyChildren = "";


            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                                    <entity name='dfe_mergeconfig'>
                                    <attribute name='dfe_mergeconfigid' />
                                    <attribute name='dfe_name' />
                                    <attribute name='dfe_requiredapplycategoryfields' />
                                    <attribute name='dfe_requiredapplyrelatedentities'/>
                                    <attribute name='dfe_extrafieldhandling' />
                                    <attribute name='dfe_relatedentitylist' />
                                    <attribute name='dfe_attributelist' />
                                    <attribute name='statuscode' />
                                    <order attribute='dfe_name' descending='false' />
                                    <filter type='and'>
                                        <condition attribute='statuscode' operator='eq' value='1' />
                                    </filter>
                                    </entity>
                                </fetch>";

            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));
            if (eCol.Entities == null) return;
            if (eCol.Entities.Count == 0) return;

            Entity entity = eCol.Entities[0];

            if (entity.Contains("dfe_attributelist")) attributeList = entity["dfe_attributelist"].ToString();
            if (entity.Contains("dfe_relatedentitylist")) relatedEntityList = entity["dfe_relatedentitylist"].ToString();
            if (entity.Contains("dfe_requiredapplycategoryfields")) requiredApplyFields = entity["dfe_requiredapplycategoryfields"].ToString();
            if (entity.Contains("dfe_extrafieldhandling")) extrafieldHanlding = entity.GetAttributeValue<string>("dfe_extrafieldhandling");
            if (entity.Contains("dfe_requiredapplyrelatedentities")) relatedApplyChildren = entity.GetAttributeValue<string>("dfe_requiredapplyrelatedentities");

            if (attributeList != null)
            {
                log.LogInformation("Processing Attribute List");
                attributeList = attributeList.Replace("\n", "").Trim();
                AttributeList = attributeList.Split(';').ToList();

                if (LogSnapshot)
                {
                    //SES fields
                    string[] sesAttributes = { "emailaddress2", "telephone1", "telephone2", "dfe_notesforclassroomexperience", "dfe_hasdbscertificate", "dfe_dateofissueofdbscertificate" };

                    foreach (string item in sesAttributes)
                    {
                        AttributeList.Add(item);
                    }
                }

                if (!AttributeList.Contains("contactid")) AttributeList.Insert(0, "contactid");

                // Basic Related Entities
                log.LogInformation("Processing Basic Related Entities");
                relatedEntityList = relatedEntityList.Replace("\n", "").Trim();
                List<string> relEnList = relatedEntityList.Split(';').ToList();
                ExtractRelatedRecordsFromList(relEnList, log);

                // Apply Related Entities
                log.LogInformation("Processing Apply Related Entities");
                relatedApplyChildren = relatedApplyChildren.Replace("\n", "").Trim();
                List<string> relAppChildren = relatedApplyChildren.Split(';').ToList();
                ExtractRelatedRecordsFromList(relAppChildren, log);

                if (requiredApplyFields.Length > 0)
                {
                    requiredApplyFields = requiredApplyFields.Replace("\n", "").Trim();
                    RequiredApplyAttributesList = requiredApplyFields.Split(';').ToList();
                }
            }

            if (!string.IsNullOrEmpty(extrafieldHanlding))
            {
                log.LogInformation("Processing extra field handling");
                List<string> extraFieldList = extrafieldHanlding.Split(';').ToList();
                for (int i = 0; i < extraFieldList.Count; i++)
                {
                    string extraFieldString = "";
                    extraFieldString = extraFieldList[i].Replace("{", "").Replace("}", "");
                    string[] extraFieldParams = extraFieldString.Split(',');

                    if (extraFieldParams.Length == 3)
                    {
                        ExtraFieldHandling toAdd = new ExtraFieldHandling();
                        toAdd.TargetField = extraFieldParams[0];
                        toAdd.OtherField = extraFieldParams[1];

                        if (extraFieldParams[2].ToLower() == "add")
                        {
                            toAdd.Operation = ExtraFieldHandlingOperationEnum.Add;
                        }

                        ExtraFieldHandlingList.Add(toAdd);
                    }
                }
            }

            log.LogInformation("Getting the metadata");
            GetMetadata(service);
            log.LogInformation("Getting the advisors");
            GetAdvisors(service);
        }

        private void ExtractRelatedRecordsFromList(List<string> relatedEntityList,ILogger log)
        {
            for (int i = 0; i < relatedEntityList.Count; i++)
            {
                log.LogInformation("Handling Related Record String {0}", relatedEntityList[i]);
                if (relatedEntityList[i] != null || (relatedEntityList[i] != null 
                    && !string.IsNullOrEmpty(relatedEntityList[i])))
                {
                    string relEntityString = "";
                    relEntityString = relatedEntityList[i].Trim().Replace("{", "").Replace("}", "");

                    if (!string.IsNullOrEmpty(relEntityString))
                    {
                        log.LogInformation("Parsing {0}", relEntityString);
                        List<string> relEnParam = relEntityString.Split(',').ToList();

                        RelatedEntity relEntity = new RelatedEntity();
                        relEntity.Name = relEnParam[0];
                        relEntity.DisplayName = relEnParam[1];
                        relEntity.FieldName = relEnParam[2];

                        RelatedEntities.Add(relEntity);
                    }
                }
            }
        }

        private void GetMetadata(IOrganizationService service)
        {
            RetrieveEntityRequest reRequest = new RetrieveEntityRequest();
            reRequest.RetrieveAsIfPublished = true;
            reRequest.LogicalName = "contact";
            reRequest.EntityFilters = EntityFilters.All;

            RetrieveEntityResponse retrieveEntityResponse = (RetrieveEntityResponse)service.Execute(reRequest);

            // Fill in the general attribute list
            foreach (string attributeName in AttributeList)
            {
                UpdateDataAttributeList(retrieveEntityResponse, Attributes, attributeName);
            }

            // Fill in the required fields attribute List
            foreach (string applyRequiredField in RequiredApplyAttributesList)
            { 
                UpdateDataAttributeList(retrieveEntityResponse, RequiredApplyAttributes, applyRequiredField);
            }
        }

        private void UpdateDataAttributeList(RetrieveEntityResponse retrieveEntityResponse, List<DMAttribute> attributes, string attributeName)
        {
            DMAttribute dma = new DMAttribute();

            dma.Name = attributeName;
            var attributeMetaData = retrieveEntityResponse.EntityMetadata.Attributes.FirstOrDefault(f => f.LogicalName == attributeName);

            if (attributeMetaData != null)
            {

                dma.Type = (DMAttributeType)Enum.Parse(typeof(DMAttributeType), attributeMetaData.AttributeType.ToString());
                dma.DisplayName = attributeMetaData.DisplayName.LocalizedLabels[0].Label;
                if (attributeMetaData.IsValidForUpdate == true) dma.IsReadOnly = false;
                else dma.IsReadOnly = true;
                attributes.Add(dma);
            }
        }

        private void GetAdvisors(IOrganizationService service)
        {
            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='true'>
                                      <entity name='systemuser'>
                                        <attribute name='fullname' />
                                        <attribute name='businessunitid' />
                                        <attribute name='systemuserid' />
                                        <order attribute='fullname' descending='false' />
                                        <link-entity name='teammembership' from='systemuserid' to='systemuserid' visible='false' intersect='true'>
                                          <link-entity name='team' from='teamid' to='teamid' alias='ap'>
                                            <filter type='and'>
                                              <condition attribute='name' operator='eq' value='Advisors' />
                                            </filter>
                                          </link-entity>
                                        </link-entity>
                                      </entity>
                                    </fetch>";

            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));
            if (eCol.Entities == null) return;
            if (eCol.Entities.Count == 0) return;

            foreach (Entity entity in eCol.Entities)
            {
                Advisors.Add(entity.Id);
            }
        }

        public async Task GetMultiOwnerContactsAsync()
        // Get list of contacts where approval email sent to Martin Dore @ TPUK
        {
            CloudBlobClient client;
            CloudBlobContainer container;

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            client = storageAccount.CreateCloudBlobClient();
            container = client.GetContainerReference("gitis-dupman-emailapprovalsent-tpuk");
            await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

            CloudBlockBlob blob = container.GetBlockBlobReference("MultipleOwnersEmailSentToTPUK.csv");
            string blobContent = await blob.DownloadTextAsync();
            List<string> idlist = new List<string>();

            if (blobContent.Length > 36)
            {
                idlist = blobContent.Split('\n').ToList();

                //     idlist = blobContent.Split(new string[] { "\n" }, StringSplitOptions.None).ToList();
                foreach (string item in idlist)
                {

                    if (item.Length < 36) continue;

                    string id = item.Substring(0, 36);
                    MultOwnersExList.Add(id);
                }
            }

        }
    }
}
