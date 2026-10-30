using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{

    public class DMRetrieveRequest
    {
        public Guid ContactId { get; set; }
        public List<string> AttributesToRetrieve { get; set; }
        public List<RelatedEntity> RelatedEntities { get; set; }

        DMLog dmLog { get; set; }
        MergeConfig mergeConfig;
        List<Guid> ownerIdList = new List<Guid>();
        int recordsOwnedByAdvisers = 0;
        DataCollection<Entity> dupEntities = null;
        public bool ManualMerge { get; set; }

        public void RunPreExecutionTest(
            IOrganizationService service, 
            Guid recordId, 
            DMLog Dmlog, 
            MergeConfig Mergeconfig,
            ILogger log)
        {
            ownerIdList.Clear();
            dmLog = Dmlog;
            mergeConfig = Mergeconfig;
            mergeConfig.GetData(service, log);

            DMRetrieveResponse dmRetrieveResponse = new DMRetrieveResponse(recordId, mergeConfig);
            List<DMEntity> dmEntityList = new List<DMEntity>();
            DMEntity masterDM = null;
            DMEntity sourceDM = null;

            try
            {
                dmRetrieveResponse.SourceEntity = service.Retrieve("contact", recordId, new ColumnSet(true));
                ownerIdList.Add(((EntityReference)dmRetrieveResponse.SourceEntity["ownerid"]).Id);


                #region Exit Clause Inactive Record
                int stateCode = ((OptionSetValue)dmRetrieveResponse.SourceEntity["statecode"]).Value;

                EntityReference er = (EntityReference)dmRetrieveResponse.SourceEntity["ownerid"];

                //Record is inactive
                if (stateCode == 1)
                {
                    dmLog.ResponseText = "Rcord is INACTIVE";
                    dmLog.ExecutionStatus = MergeStatus.SKIP_InactiveRecord;
                    return;
                }

                #endregion Exit Clause Inactive Record

                #region Retrieve

                RetrieveDuplicatesRequest dupRequest = new RetrieveDuplicatesRequest();
                Entity entity = new Entity("contact", recordId);
                dupRequest.BusinessEntity = entity;
                dupRequest.MatchingEntityName = entity.LogicalName;
                dupRequest.PagingInfo = new PagingInfo() { PageNumber = 1, Count = 50 };

                RetrieveDuplicatesResponse retrieveDuplicateResponse = (RetrieveDuplicatesResponse)service.Execute(dupRequest);

                #region Exit Clause No Duplicates

                //No Duplicates
                if (retrieveDuplicateResponse != null) dupEntities = retrieveDuplicateResponse.DuplicateCollection.Entities;
                if (dupEntities == null || dupEntities.Count == 0)
                {
                    dmLog.ResponseText = "No Duplicates Found";
                    dmLog.ExecutionStatus = MergeStatus.SKIP_NoDuplicates;
                    return;
                }

                //No Active Duplicates
                bool anyActiveEntity = false;
                foreach (Entity item in dupEntities)
                {
                    if (((OptionSetValue)item["statecode"]).Value == 0)
                    {
                        anyActiveEntity = true;
                        break;
                    }

                    if (anyActiveEntity == false)
                    {
                        dmLog.ResponseText = "No Duplicates Found";
                        dmLog.ExecutionStatus = MergeStatus.SKIP_NoDuplicates;
                        return;
                    }
                }

                #endregion Exit Clause No Duplicates

                #endregion Retrieve

                #region Set master and reset

                sourceDM = new DMEntity(dmRetrieveResponse.SourceEntity, mergeConfig);
                dmRetrieveResponse.SourceDM = sourceDM;
                dmEntityList.Add(sourceDM);
                masterDM = sourceDM; //Set the same initially
                dmRetrieveResponse.MasterEntity = dmRetrieveResponse.SourceEntity;

                if (sourceDM.IsOwnedByAdviser && sourceDM.IsAdviserStatusClosed == false) recordsOwnedByAdvisers += 1;
                if (!ownerIdList.Contains(sourceDM.Id)) ownerIdList.Add(sourceDM.Id);

                int highestScore = sourceDM.DuplicateScore;

                foreach (Entity item in retrieveDuplicateResponse.DuplicateCollection.Entities)
                {
                    if (((OptionSetValue)item["statecode"]).Value == 1) continue;

                    dmLog.NoOfDuplicates += 1;

                    DMEntity dme = new DMEntity(item, mergeConfig);

                    if (dme.DuplicateScore > highestScore)
                    {
                        masterDM = dme;
                        highestScore = dme.DuplicateScore;
                        dmEntityList.Insert(1, dme);
                        dmRetrieveResponse.MasterEntity = item;
                        dmRetrieveResponse.DuplicateEntities.Insert(0, item);
                    }
                    else
                    {
                        dmEntityList.Add(dme);
                        dmRetrieveResponse.DuplicateEntities.Add(item);
                    }

                    if (dme.IsOwnedByAdviser && dme.IsAdviserStatusClosed == false) recordsOwnedByAdvisers += 1;
                }

                if (recordsOwnedByAdvisers > 1) dmRetrieveResponse.HasMultipleOwners = true;

                dmRetrieveResponse.MasterDM = masterDM;
                dmLog.MasterId = dmRetrieveResponse.MasterEntity.Id.ToString();
                dmRetrieveResponse.DMEntities = dmEntityList;

                ResetMaster(dmRetrieveResponse);
                dmRetrieveResponse.GettIttProviderAppliedTo(service);

                #endregion

                dmRetrieveResponse.Success = true;

            }
            catch (Exception ex)
            {
                dmRetrieveResponse.Success = false;
            }
        }

        public async Task<DMRetrieveResponse> SendAsync(
            IOrganizationService service, 
            ILogger logger, 
            Guid recordId, 
            DMLog Dmlog, 
            MergeConfig Mergeconfig, 
            Guid[] otherRecordIds = null)
        {
            logger.LogInformation("Entering: Task<DMRetrieveResponse> SendAsync");
            ownerIdList.Clear();
            dmLog = Dmlog;
            mergeConfig = Mergeconfig;
            mergeConfig.GetData(service, logger);

            if (mergeConfig.MultOwnersExList.Any(a => a == recordId.ToString()))
            {
                dmLog.ExecutionStatus = MergeStatus.SKIP_MultipleOwners;
            }

            DMRetrieveResponse dmRetrieveResponse = new DMRetrieveResponse(recordId, mergeConfig);
            List<DMEntity> dmEntityList = new List<DMEntity>();
            DMEntity masterDM = null;
            DMEntity sourceDM = null;

            try
            {
                dmRetrieveResponse.SourceEntity = service.Retrieve("contact", recordId, new ColumnSet(true));
                ownerIdList.Add(((EntityReference)dmRetrieveResponse.SourceEntity["ownerid"]).Id);


                #region Exit Clause
                int stateCode = ((OptionSetValue)dmRetrieveResponse.SourceEntity["statecode"]).Value;

                EntityReference er = (EntityReference)dmRetrieveResponse.SourceEntity["ownerid"];

                //Record is inactive
                if (stateCode == 1)
                {
                    dmLog.ResponseText = "Rcord is INACTIVE";
                    dmLog.Trace(dmLog.ResponseText);
                    dmLog.ExecutionStatus = MergeStatus.SKIP_InactiveRecord;
                    return null;
                }

                #endregion Exit Clause

                #region Retrieve
                if (otherRecordIds == null || otherRecordIds?.Length == 0)
                {
                    logger.LogInformation("No Other Ids Present. Retrieving Default response");
                    RetrieveDuplicatesRequest dupRequest = new RetrieveDuplicatesRequest();
                    Entity entity = new Entity("contact", recordId);
                    dupRequest.BusinessEntity = entity;
                    dupRequest.MatchingEntityName = entity.LogicalName;
                    dupRequest.PagingInfo = new PagingInfo() { PageNumber = 1, Count = 50 };

                    RetrieveDuplicatesResponse retrieveDuplicateResponse = (RetrieveDuplicatesResponse)service.Execute(dupRequest);

                    if (retrieveDuplicateResponse != null) dupEntities = retrieveDuplicateResponse.DuplicateCollection.Entities;
                }
                else
                {
                    logger.LogInformation("Ohter Ids are present so retrieving those");
                    RetrieveMultipleRequest request = new RetrieveMultipleRequest();
                    QueryExpression query = new QueryExpression("contact");
                    query.ColumnSet = new ColumnSet(true);
                    query.Criteria = new FilterExpression();
                    query.Criteria.Conditions.Add(new ConditionExpression("contactid", ConditionOperator.In, otherRecordIds));
                    
                    request.Query = query;
                    RetrieveMultipleResponse retrieveMultipleResponse = (RetrieveMultipleResponse)service.Execute(request);
                    dupEntities = retrieveMultipleResponse.EntityCollection.Entities;
                }

                #region Exit Clause 2

                //No Duplicates

                if (dupEntities == null || dupEntities.Count == 0)
                {
                    logger.LogInformation("No other duplicates detected");
                    dmLog.ResponseText = "No Duplicates Found";
                    dmLog.ExecutionStatus = MergeStatus.SKIP_NoDuplicates;


                    if (ManualMerge)
                    {
                        logger.LogInformation("Manual Merge is true so getting Source Entity Data");
                        GetSourceEntityData(dmRetrieveResponse, service, logger);
                        dmRetrieveResponse.ResponseText = "No Duplicates Found";
                        return dmRetrieveResponse;
                    }

                    return null;
                }

                //No Active Duplicates
                bool anyActiveEntity = false;
                foreach (Entity item in dupEntities)
                {
                    if (((OptionSetValue)item["statecode"]).Value == 0)
                    {
                        anyActiveEntity = true;
                        break;
                    }
                }


                if (anyActiveEntity == false)
                {
                    dmLog.ResponseText = "No Duplicates Found";
                    dmLog.ExecutionStatus = MergeStatus.SKIP_NoDuplicates;

                    if (ManualMerge)
                    {
                        GetSourceEntityData(dmRetrieveResponse, service, logger);
                        dmRetrieveResponse.ResponseText = "No Duplicates Found";
                        return dmRetrieveResponse;
                    }

                    return null;
                }

                #endregion Exit Clause 2


                #endregion Retrieve

                #region Continue Process

                sourceDM = new DMEntity(dmRetrieveResponse.SourceEntity, mergeConfig);
                dmRetrieveResponse.SourceDM = sourceDM;
                dmEntityList.Add(sourceDM);
                masterDM = sourceDM; //Set the same initially
                dmRetrieveResponse.MasterEntity = dmRetrieveResponse.SourceEntity;

                if (sourceDM.IsOwnedByAdviser && sourceDM.IsAdviserStatusClosed == false) recordsOwnedByAdvisers += 1;
                if (!ownerIdList.Contains(sourceDM.Id)) ownerIdList.Add(sourceDM.Id);

                int highestScore = sourceDM.DuplicateScore;

                foreach (Entity item in dupEntities)
                {
                    if (((OptionSetValue)item["statecode"]).Value == 1) continue;

                    // if this item has been marked as not a duplicate then skip
                    if (item.Contains("dfe_isnotaduplicate")
                        && (((bool)item["dfe_isnotaduplicate"]) == true))
                        continue;

                    dmLog.NoOfDuplicates += 1;

                    DMEntity dme = new DMEntity(item, mergeConfig);

                    if (dme.DuplicateScore > highestScore)
                    {
                        masterDM = dme;
                        highestScore = dme.DuplicateScore;
                        dmEntityList.Insert(1, dme);
                        dmRetrieveResponse.DuplicateEntities.Insert(0, dmRetrieveResponse.MasterEntity);
                        dmRetrieveResponse.MasterEntity = item;
                    }
                    else
                    {
                        dmEntityList.Add(dme);
                        dmRetrieveResponse.DuplicateEntities.Add(item);
                    }

                    if (dme.IsOwnedByAdviser && dme.IsAdviserStatusClosed == false) recordsOwnedByAdvisers += 1;
                }

                if (recordsOwnedByAdvisers > 1) dmRetrieveResponse.HasMultipleOwners = true;

                dmRetrieveResponse.MasterDM = masterDM;
                dmRetrieveResponse.DMEntities = dmEntityList;

                ResetMaster(dmRetrieveResponse);
                dmRetrieveResponse.GettIttProviderAppliedTo(service);
                dmLog.MasterId = dmRetrieveResponse.MasterEntity.Id.ToString();

                // Flag Attribute Differences Here
                FlagAttributeDifference(sourceDM, dmEntityList, mergeConfig);

                //if (mergeConfig.LogSnapshot || ManualMerge)
                //{
                    foreach (RelatedEntity item in mergeConfig.RelatedEntities)
                    {
                        foreach (DMEntity dme in dmRetrieveResponse.DMEntities)
                        {
                            RelatedEntity relatedEntity = new RelatedEntity
                            {
                                Name = item.Name,
                                RecordName = item.DisplayName,
                                FieldName = item.FieldName,
                                DisplayName = item.DisplayName
                            };
                            dme.RelatedEntities.Add(relatedEntity);
                        }
                        item.GetData(service, dmRetrieveResponse.DMEntities,logger);
                    }

                    dmRetrieveResponse.GetMarketingEvents(service);
                //}


                #endregion

            }
            catch (Exception ex)
            {
                dmLog.ExecutionStatus = MergeStatus.FAIL;
                dmLog.LoggedBy = LoggedByFunction.AFProcessDuplicate;
                if (ex.ToString().Length > 500) dmLog.ResponseText = ex.ToString().Substring(0, 500);
                else dmLog.ResponseText = ex.ToString();
                dmLog.Trace($"ERROR: {ex.ToString()}");
                await dmLog.WriteExecutionLogAsync();
                return null;
            }

            return dmRetrieveResponse;

        }

        private void ResetMaster(DMRetrieveResponse dmRetrieveResponse)
        {
            //If duplicates has a single adviser as owner and candidate adviser status is open then set that record as master

            if (recordsOwnedByAdvisers != 1) return;
            if (dmRetrieveResponse.MasterDM.IsOwnedByAdviser && dmRetrieveResponse.MasterDM.IsAdviserStatusClosed == false) return;


            int dupOwnedByAdviserAndOpen = 0;
            DMEntity dupDMEOwnedByAdviser = null;

            foreach (var item in dmRetrieveResponse.DMEntities)
            {
                if (item.IsOwnedByAdviser == true && item.IsAdviserStatusClosed == false)
                {
                    dupOwnedByAdviserAndOpen += 1;
                    dupDMEOwnedByAdviser = item;
                }
            }
            if (dupOwnedByAdviserAndOpen > 1 || dupDMEOwnedByAdviser == null) return;

            dmRetrieveResponse.MasterDM = dupDMEOwnedByAdviser;
            dmRetrieveResponse.DuplicateEntities.Insert(0, dmRetrieveResponse.MasterEntity);
            dmRetrieveResponse.MasterEntity = dmRetrieveResponse.DuplicateEntities.FirstOrDefault(f => f.Id == dupDMEOwnedByAdviser.Id);
            dmRetrieveResponse.DuplicateEntities.Remove(dmRetrieveResponse.MasterEntity);


        }

        private void GetSourceEntityData(
            DMRetrieveResponse dmRetrieveResponse, 
            IOrganizationService service,
            ILogger logger)
        {

            DMEntity sourceDM = new DMEntity(dmRetrieveResponse.SourceEntity, mergeConfig);
            dmRetrieveResponse.DMEntities = new List<DMEntity>();
            dmRetrieveResponse.DMEntities.Add(sourceDM);

            foreach (RelatedEntity item in mergeConfig.RelatedEntities)
            {
                logger.LogInformation("Retrieving Related Entity {0}", item.Name);
                RelatedEntity relatedEntity = new RelatedEntity
                {
                    Name = item.Name,
                    RecordName = item.DisplayName,
                    FieldName = item.FieldName,
                    DisplayName = item.DisplayName
                };
                sourceDM.RelatedEntities.Add(relatedEntity);
                item.GetData(service, dmRetrieveResponse.DMEntities, logger);
            }

            dmRetrieveResponse.GetMarketingEvents(service);
        }

        private void FlagAttributeDifference(DMEntity sourceItem, List<DMEntity> dMEntities, MergeConfig mergeConfig)
        {

            foreach (DMEntity entity in dMEntities)
            {
                // Don't compare the source to itself
                if (sourceItem.Id == entity.Id)
                    continue;

                foreach (var attribute in mergeConfig.Attributes)
                {
                    IsAttributeDifferent(sourceItem, entity, attribute);
                }

                foreach (var attribute in mergeConfig.RequiredApplyAttributes)
                {
                    IsAttributeDifferent(sourceItem, entity, attribute);
                }
            }
        }

        private void IsAttributeDifferent(DMEntity sourceItem, DMEntity comparingEntity, DMAttribute configDma)
        {
            var sourceDma = sourceItem.DMAttributes?.FirstOrDefault(x => x.Name.CompareTo(configDma.Name) == 0);
            var targetDma = comparingEntity.DMAttributes?.FirstOrDefault(x => x.Name.CompareTo(configDma.Name) == 0);
            bool isDifferent = false;

            if (IfBothEmpty(sourceDma, targetDma))
                return;

            if (IfEitherEmpty(sourceDma, targetDma))
            {
                isDifferent = true;
            }
            else
            {
                if (configDma.Type == DMAttributeType.Uniqueidentifier)
                {
                    return;
                }
                else if (configDma.Type == DMAttributeType.Lookup
                         || configDma.Type == DMAttributeType.Owner
                         || configDma.Type == DMAttributeType.Customer)
                {
                    isDifferent = ((Guid)sourceDma.Value).CompareTo((Guid)targetDma.Value) != 0;
                }
                else if (configDma.Type == DMAttributeType.Picklist || configDma.Type == DMAttributeType.State)
                {
                    isDifferent = ((int)sourceDma.Value).CompareTo((int)targetDma.Value) != 0;
                }
                else if (configDma.Type == DMAttributeType.Boolean)
                {
                    bool sourceBol = (bool)sourceDma.Value;
                    bool targetBol = (bool)targetDma.Value;

                    isDifferent = sourceBol.CompareTo(targetBol) != 0;

                }
                else if (configDma.Type == DMAttributeType.DateTime)
                {
                    DateTime sourceDateTime = (DateTime)sourceDma.Value;
                    DateTime targetDateTime = (DateTime)targetDma.Value;

                    isDifferent = sourceDateTime.CompareTo(targetDma.Value) != 0;
                }
                else if (configDma.Type == DMAttributeType.Money)
                {
                    Money sourceMon = (Money)sourceDma.Value;
                    Money targetMon = (Money)targetDma.Value;

                    isDifferent = sourceMon?.Value.CompareTo(targetMon?.Value) != 0;
                }
                else if (configDma.Type == DMAttributeType.BigInt || configDma.Type == DMAttributeType.Decimal || configDma.Type == DMAttributeType.Double
                        || configDma.Type == DMAttributeType.Integer || configDma.Type == DMAttributeType.Memo || configDma.Type == DMAttributeType.String)
                {
                    string sourceString = sourceDma.Value?.ToString();
                    string targetString = targetDma.Value?.ToString();

                    isDifferent = sourceString.CompareTo(targetString) != 0;
                }
            }

            if (isDifferent)
            {
                if (targetDma == null)
                {
                    comparingEntity.DMAttributes.Add(new DMAttribute()
                    {
                        IsDifferentFromCurrentRecord = true,
                        Name = configDma.Name,
                        Type = configDma.Type,
                        IsReadOnly = true,
                        Value = null,
                        FormattedValue = null,
                    });
                }
                else
                {

                    targetDma.IsDifferentFromCurrentRecord = true;
                }
            }
        }

        private static bool IfBothEmpty(DMAttribute sourceDma, DMAttribute targetDma)
        {
            bool sourceDmaIsNull = sourceDma?.Value == null;
            bool targetDmaIsNull = targetDma?.Value == null;

            if (sourceDmaIsNull && targetDmaIsNull)
                return true;

            return false;
        }

        private static bool IfEitherEmpty(DMAttribute sourceDma, DMAttribute targetDma)
        {
            bool sourceDmaIsNull = sourceDma?.Value == null;
            bool targetDmaIsNull = targetDma?.Value == null;

            if ((!sourceDmaIsNull && targetDmaIsNull) || (sourceDmaIsNull && !targetDmaIsNull))
                return true;

            return false;
        }
    }

    public class DMRetrieveResponse
    {
        public Entity SourceEntity { get; set; }
        public string SourceRecordUrl { get; private set; }
        public Entity MasterEntity { get; set; }
        public List<Entity> DuplicateEntities = new List<Entity>();

        public DMEntity MasterDM { get; set; }
        public DMEntity SourceDM { get; set; }
        public List<DMEntity> DMEntities = new List<DMEntity>();
        public DMEntity MergedDMEntity { get; set; }
        public string Html = "";
        public bool HasMultipleOwners { get; set; }
        public List<ITTAppliedTo> ITTAppliedToList = new List<ITTAppliedTo>();
        public string ResponseText { get; set; }
        public bool Success { get; set; }

        private string crmUrl = "";
        MergeConfig mergeConfig;

        public DMRetrieveResponse(Guid callingEntityId, MergeConfig Mergeconfig)
        {
            crmUrl = Environment.GetEnvironmentVariable("D365_APPURL") + "&amp;pagetype=entityrecord&amp;etn=contact&amp;id=";
            SourceRecordUrl = Environment.GetEnvironmentVariable("D365_APPURL") + "&pagetype=entityrecord&etn=contact&id=" + callingEntityId.ToString();
            mergeConfig = Mergeconfig;
        }

        #region CSS
        private const string css = @" <style> 
        .bold {
            font-weight: bold;
        }

        .tblDup {
            border-collapse: collapse;
            border-spacing: 0;
            font-family: Tahoma, Arial, Helvetica, sans-serif;
            overflow: auto;
            display: block;
        }
        
        .tblDup td {
            font-family: Tahoma, Arial, Helvetica, sans-serif;
            font-size: 12px;
            padding: 4px;
            min-width: 234px;
            max-width: 250px;
            border: 1px solid #ddd;
            height: 28px;
            vertical-align: top;
            padding-top: 6px;
        }
        
        .tblDup th {
            text-align: left;
            background-color: #A9DFBF;
            color: #505050;
            border-left: 1px solid #ddd;
            border-right: 1px solid #ddd;
            padding-left: 8px;
            padding-right: 8px;
            font-size: 12px;
            height: 30px;
            box-shadow: 0px 1px #ddd;
        }
        
        .tblDup tr td:first-child {
            background-color: #F0F0F0;
            vertical-align: top;
            padding-top: 6px;
            min-width: 234px;
            padding-left: 8px;
        }
        
        .tblDup thead th {
            position: sticky;
            top: 0;
        }
        
        .tdExcluded {
            color: white;
            font-weight: bold;
            background-color: #F67E72;
        }
        
        .tdIncluded {
            color: white;
            font-weight: bold;
            background-color: #99D592;
        }
        
        .tdIncluded:hover,
        .tdExcluded:hover {
            cursor: pointer;
        }
        
        .tblDup tr td ul {
            margin-top: 6px;
        }
        
        .tblDup tr td h4 {
            padding-bottom: 4px;
            margin: 0px;
            font-size: 12px;
            color: #808080;
        }
        
        .tblDup tr td ul {
            font-size: 12px;
            color: #808080;
        }
        
        .tblDup tr td li:last-child {}
        
        .tblDup tr td ul li {
            list-style-type: square;
            padding-left: 4px;
            margin-left: -30px;
        }
        
        .tblDup tr td ol li {
            list-style-type: decimal;
            padding-left: 4px;
            margin-left: -40px;
        }
        
        .selected {
            background-color: #FDF7F5;
        }
        
        .excluded {
            color: #BFBFBF;
        }
        
        .included {
            color: black;
        }
        
        
        .readOnly {
            color: #BFBFBF;
        }
        
        .selectable {
            color: black;
        }
               
        .overridden {
            background-color: #FFF9C4;
        }
        
        .dvActiveTotal {
            color: white;
            border: none;
            padding: 4px;
            border-radius: 2px;
            background-color: #79BE82;
            float: left;
            min-width: 14px;
            height: 14px;
            text-align: center;
            margin-right: 4px;
            font-size: 12px;
        }
        
        .dvInactiveTotal {
            color: white;
            border: none;
            padding: 4px;
            border-radius: 2px;
            background-color: #ABAFAB;
            float: left;
            min-width: 14px;
            height: 14px;
            text-align: center;
            margin-right: 4px;
            font-size: 12px;
        }
        
        .spnScore {
            padding: 4px;
            border-radius: 2px;
            background-color: #F67E72;
            float: right;
            min-width: 14px;
            height: 14;
            text-align: center;
            margin-top: 2px;
            color: white;
            font-weight: lighter;
            font-size: 12px;
        }
        
        
        p {
            font-family: Tahoma, Arial, Helvetica, sans-serif;
            font-size: 14px;
            font-weight: normal;
        }
        
        
        .dvCustomerJourney {
            display: block;
        }
        
        .dvCJTotal {
            color: white;
            border: none;
            padding: 4px;
            border-radius: 25px;
            background-color: #79BE82;
            display: block;
            width: 22px;
            text-align: center;
            vertical-align: middle;
            margin-right: 4px;
            margin-bottom: 4px;
        }
        
        .spnExpand {
            cursor: pointer;
            font-weight: bold;
            color: #EA8A7D;
            float: right;
            font-size: 16px;
        }
        
       
        .spnNumber {
            margin-left: 8px;
            color: white;
            background-color: #EA8A7D;
            padding-left: 3px;
            padding-right: 3px;
            text-align: center;
            padding-bottom: 1px;
            border-radius: 2px;
        }
                
        .dvTh {
            color: #E7714B;
            clar: both;
            padding-left: 8px;
            textalign: left;
            font-size: 12px;
            margin-top: 6px;
            margin-bottom: 4px;
            background-color: #FFE3DC;
        }
        
        .dvTr {
            font-size: 12px;
            color: #E7714B;
            text-align: left;
            padding: 2px 0px 2px 10px;
            margin-left: 8px;
            border-bottom: 1px solid white;
        }
        
        .green {
            color: #2C7616;
            background-color: #E9F7E5;
        }
        
        .greenNoBg {
            color: #2C7616;
        }
		.rowH2{
			float:left; 
			padding-top:6px
		}
        </style>";

        #endregion

        List<Guid> dupGuids = new List<Guid>();

        public void GetMarketingEvents(IOrganizationService service)
        {

            foreach (DMEntity dme in DMEntities)
            {
                dupGuids.Add(dme.Id);
            }


            QueryExpression qe = new QueryExpression();
            ConditionExpression ce = new ConditionExpression();
            FilterExpression fe = new FilterExpression();
            OrderExpression oe = new OrderExpression();

            qe = new QueryExpression();
            qe.EntityName = "msevtmgt_eventregistration";
            qe.ColumnSet = new ColumnSet("msevtmgt_name", "msevtmgt_eventid", "msevtmgt_contactid", "createdon");

            ce.AttributeName = "msevtmgt_contactid";
            ce.Operator = ConditionOperator.In;

            foreach (DMEntity item in DMEntities)
            {
                ce.Values.Add(item.Id);
            }
            fe.Conditions.Add(ce);
            fe.Conditions.Add(new ConditionExpression("statecode", ConditionOperator.Equal, 0));
            qe.Criteria.AddFilter(fe);

            oe.AttributeName = "createdon";
            oe.OrderType = OrderType.Descending;
            qe.Orders.Add(oe);

            EntityCollection ecol = service.RetrieveMultiple(qe);

            if (ecol.Entities.Count > 0)
            {

                foreach (DMEntity dme in DMEntities)
                {
                    dme.MarketingEvents = new List<MarketingEvent>();
                }

                foreach (Entity entity in ecol.Entities)
                {
                    if (entity.Attributes.Contains("msevtmgt_contactid"))
                    {
                        string contactId = ((EntityReference)entity["msevtmgt_contactid"]).Id.ToString();
                        DMEntity dme = null;

                        if (entity.Attributes.Contains("msevtmgt_eventid"))
                        {
                            MarketingEvent mEvent = new MarketingEvent();
                            mEvent.EventRegistrations = new List<EventRegistration>();

                            EntityReference erMarketingEvent = (EntityReference)entity["msevtmgt_eventid"];
                            mEvent.Id = erMarketingEvent.Id.ToString();
                            mEvent.Name = erMarketingEvent.Name;
                            mEvent.ContactId = contactId;

                            EventRegistration eventReg = new EventRegistration();
                            if (entity.Attributes.Contains("msevtmgt_name")) eventReg.Name = entity["msevtmgt_name"].ToString();
                            else eventReg.Name = "";
                            eventReg.Id = entity.Id.ToString();
                            eventReg.ContactId = contactId;

                            dme = DMEntities.FirstOrDefault(f => f.Id == new Guid(contactId));

                            if (dme == null) continue;

                            MarketingEvent addedEvent = dme.MarketingEvents.FirstOrDefault(f => f.Id == mEvent.Id);

                            if (addedEvent != null) mEvent = addedEvent;
                            else dme.MarketingEvents.Add(mEvent);

                            eventReg.EventId = mEvent.Id;
                            mEvent.EventRegistrations.Add(eventReg);
                            GetChecIn(mEvent, service);
                        }
                    }
                }
            }
        }

        public void GettIttProviderAppliedTo(IOrganizationService service)
        {

            foreach (DMEntity dme in DMEntities)
            {
                dupGuids.Add(dme.Id);
            }

            QueryExpression qe = new QueryExpression();
            ConditionExpression ce = new ConditionExpression();
            FilterExpression fe = new FilterExpression();
            OrderExpression oe = new OrderExpression();

            qe = new QueryExpression();
            qe.EntityName = "dfe_teachingcollegeappliedto";
            qe.ColumnSet = new ColumnSet("dfe_name", "dfe_dateapplied", "dfe_dateaccepted", "dfe_contactid", "createdon", "statecode");

            ce.AttributeName = "dfe_contactid";
            ce.Operator = ConditionOperator.In;

            foreach (DMEntity item in DMEntities)
            {
                ce.Values.Add(item.Id);
            }
            fe.Conditions.Add(ce);
            fe.Conditions.Add(new ConditionExpression("statecode", ConditionOperator.Equal, 0));
            qe.Criteria.AddFilter(fe);

            oe.AttributeName = "createdon";
            oe.OrderType = OrderType.Descending;
            qe.Orders.Add(oe);

            EntityCollection ecol = service.RetrieveMultiple(qe);

            if (ecol.Entities.Count > 0)
            {

                foreach (var item in ecol.Entities)
                {
                    ITTAppliedTo itt = new ITTAppliedTo(item);
                    if (itt.DateApplied == DateTime.MinValue && itt.DateAccepted == DateTime.MinValue) continue;
                    //  if (itt.ContactId == MasterEntity.Id) continue;

                    ITTAppliedToList.Add(itt);
                }
            }
        }

        private void GetChecIn(MarketingEvent marketingEvent, IOrganizationService service)
        {
            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                                          <entity name='msevtmgt_checkin'>
                                            <attribute name='msevtmgt_checkinid' />
                                            <attribute name='msevtmgt_name' />
                                            <attribute name='msevtmgt_checkintime' />
                                            <attribute name='msevtmgt_registrationid' />
                                            <attribute name='msevtmgt_event' />
                                            <attribute name='msevtmgt_contact' />
                                            <order attribute='msevtmgt_name' descending='false' />
                                            <filter type='and'>
                                              <condition attribute='msevtmgt_event' operator='eq' uitype='msevtmgt_event' value='{" + marketingEvent.Id + @"}' />
                                              <condition attribute='msevtmgt_contact' operator='eq' uitype='contact' value='{" + marketingEvent.ContactId + @"}' />
                                            </filter>
                                          </entity>
                                        </fetch>";

            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));

            if (eCol != null)
            {
                if (eCol.Entities.Count > 0)
                {
                    foreach (Entity entity in eCol.Entities)
                    {
                        string eventRegId = "";

                        if (entity.Attributes.Contains("msevtmgt_checkintime"))
                        {
                            marketingEvent.CheckedIn = true;
                            if (entity.Attributes.Contains("msevtmgt_registrationid"))
                            {
                                eventRegId = ((EntityReference)entity["msevtmgt_registrationid"]).Id.ToString();
                                EventRegistration eventReg = marketingEvent.EventRegistrations.FirstOrDefault(f => f.Id == eventRegId);

                                if (eventReg != null) eventReg.CheckedIn = true;
                            }
                        }

                        else if (marketingEvent.CheckedIn != true) marketingEvent.CheckedIn = false;
                    }
                }
                else marketingEvent.CheckedIn = false;
            }
            else marketingEvent.CheckedIn = false;
        }

        private string GetHtmlString()
        {

            int counter = 0;

            string tblString = "<table id='tblDup' class='tblDup'><thead><tr>" + Environment.NewLine +
                 "<th>Attribute Name</th>" + Environment.NewLine +
                @"<th><div class='rowH2'><a href='" + crmUrl + MasterDM.Id.ToString() + "' target='_blank'>Master Record (Pre-Merge)</a></div><span class='spnScore'>" +
                MasterDM.DuplicateScore + "</span></th>" + Environment.NewLine +
               @"<th><div class='rowH2'><a href='" + crmUrl + MergedDMEntity.Id.ToString() + "' target='_blank'>Master Record (Post-Merge)</a></div><span class='spnScore'>" +
               MergedDMEntity.DuplicateScore + "</span></th>" + Environment.NewLine;

            foreach (DMEntity item in DMEntities)
            {
                string headerName = "";
                if (item.Id == MasterDM.Id) continue;

                counter++;
                if (item.Id == SourceDM.Id) headerName = counter.ToString() + " (Source Record)";
                else headerName = counter.ToString();

                tblString += @"<th><div class='rowH2'><a href='" + crmUrl + item.Id.ToString() + "' target='_blank'>Duplicate " + headerName + "</a></div><span class='spnScore'>" +
                    item.DuplicateScore + "</span></th>" + Environment.NewLine;
            }

            tblString += "</tr>" + Environment.NewLine + "</thead>" + Environment.NewLine + "<tbody>" + Environment.NewLine;

            #region Attributes


            foreach (DMAttribute dma in mergeConfig.Attributes)
            {
                if (dma.Name == "dfe_duplicatescorecalculated") continue;

                tblString += "<tr>" + Environment.NewLine + "<td>" + Environment.NewLine + dma.DisplayName + "</td>" + Environment.NewLine;

                string className = "selectable";
                DMAttribute dmaMaster = null;

                //master Entity
                dmaMaster = MasterDM.DMAttributes.FirstOrDefault(f => f.Name == dma.Name);
                if (dmaMaster == null)
                {
                    DMAttribute dmatt = new DMAttribute();
                    dmatt.Name = dma.Name;
                    dmatt.DisplayName = dma.DisplayName;
                    dmatt.Type = dma.Type;
                    dmatt.Value = null;
                    dmatt.FormattedValue = null;
                    dmatt.IsReadOnly = dma.IsReadOnly;
                    SourceDM.DMAttributes.Add(dmatt);
                    dmaMaster = dmatt;
                }

                if (dmaMaster.IsReadOnly) className = "readOnly";
                tblString += "<td class='" + className + "'>" + dmaMaster.FormattedValue + "</td>" + Environment.NewLine;

                //Merged Entity
                DMAttribute dmAttMerged = null;
                dmAttMerged = MergedDMEntity.DMAttributes.FirstOrDefault(f => f.Name == dma.Name);
                if (dmAttMerged == null)
                {
                    DMAttribute dmatt = new DMAttribute();
                    dmatt.Name = dma.Name;
                    dmatt.DisplayName = dma.DisplayName;
                    dmatt.Type = dma.Type;
                    dmatt.Value = null;
                    dmatt.FormattedValue = null;
                    dmatt.IsReadOnly = dma.IsReadOnly;
                    MergedDMEntity.DMAttributes.Add(dmatt);
                    dmAttMerged = dmatt;
                }

                if (dmAttMerged.IsReadOnly) className = "readOnly";

                tblString += "<td class='" + className + "'>" + dmAttMerged.FormattedValue + "</td>" + Environment.NewLine;

                foreach (DMEntity dme in DMEntities)
                {
                    DMAttribute dmAtt = null;

                    if (dme.Id == MasterDM.Id) continue;

                    dmAtt = dme.DMAttributes.FirstOrDefault(f => f.Name == dma.Name);

                    if (dmAtt == null)
                    {
                        DMAttribute dmatt = new DMAttribute();
                        dmatt.Name = dma.Name;
                        dmatt.DisplayName = dma.DisplayName;
                        dmatt.Type = dma.Type;
                        dmatt.Value = null;
                        dmatt.FormattedValue = null;
                        dmatt.IsReadOnly = dma.IsReadOnly;
                        MergedDMEntity.DMAttributes.Add(dmatt);
                        dmAtt = dmatt;
                    }

                    if (dmAtt.IsReadOnly) className = "readOnly";
                    tblString += "<td class='" + className + "'>" + dmAtt.FormattedValue + "</td>" + Environment.NewLine;
                }
                tblString += "</tr>" + Environment.NewLine;
            }

            #endregion

            #region Marketingdata

            tblString += "<tr>" + Environment.NewLine + "<td>Event Registrations</td>" + Environment.NewLine;

            string tdString = "";

            //Source Entity
            tdString = "<td>Events <span class='spnNumber'>" + MasterDM.MarketingEvents.Count.ToString() + "</span>" + Environment.NewLine;

            if (MasterDM.MarketingEvents.Count > 0)
            {
                foreach (MarketingEvent mEvent in MasterDM.MarketingEvents)
                {
                    tdString += "<div class='dvTh'>" + mEvent.Name + "</div>" + Environment.NewLine;

                    foreach (EventRegistration eReg in mEvent.EventRegistrations)
                    {
                        tdString += "<div class='dvTr'>" + eReg.Name + "</div>" + Environment.NewLine;
                    }
                }
            }
            tdString += "</td>" + Environment.NewLine;
            tblString += tdString;

            //Merged Entity
            tdString = "<td>Events <span class='spnNumber'>" + MergedDMEntity.MarketingEvents.Count.ToString() + "</span>" + Environment.NewLine;

            if (MergedDMEntity.MarketingEvents.Count > 0)
            {
                foreach (MarketingEvent mEvent in MergedDMEntity.MarketingEvents)
                {
                    tdString += "<div class='dvTh'>" + mEvent.Name + "</div>" + Environment.NewLine;

                    foreach (EventRegistration eReg in mEvent.EventRegistrations)
                    {
                        tdString += "<div class='dvTr'>" + eReg.Name + "</div>" + Environment.NewLine;
                    }
                }
            }
            tdString += "</td>" + Environment.NewLine;
            tblString += tdString;

            //Duplicate Entities
            foreach (DMEntity dme in DMEntities)
            {
                if (dme.Id == MasterDM.Id) continue;
                tdString = "<td>Events <span class='spnNumber'>" + dme.MarketingEvents.Count.ToString() + "</span>" + Environment.NewLine;

                if (dme.MarketingEvents.Count > 0)
                {
                    foreach (MarketingEvent mEvent in dme.MarketingEvents)
                    {
                        tdString += "<div class='dvTh'>" + mEvent.Name + "</div>" + Environment.NewLine;

                        foreach (EventRegistration eReg in mEvent.EventRegistrations)
                        {
                            tdString += "<div class='dvTr'>" + eReg.Name + "</div>" + Environment.NewLine;
                        }
                    }
                }
                tdString += "</td>" + Environment.NewLine;
                tblString += tdString;
            }
            tblString += "</tr>" + Environment.NewLine;

            #endregion

            #region RelatedEntity

            foreach (RelatedEntity ren in mergeConfig.RelatedEntities)
            {
                tblString += "<tr>" + Environment.NewLine + "<td>" + ren.DisplayName + "</td>" + Environment.NewLine;

                //Source Entity
                RelatedEntity relEn = SourceDM.RelatedEntities.FirstOrDefault(f => f.Name == ren.Name);

                tblString += "<td>" + Environment.NewLine + relEn.DisplayName + "<div class=dvActiveTotal>" + relEn.ActiveTotal + "</div>" + Environment.NewLine +
                "<div class='dvInactiveTotal'>" + relEn.InactiveTotal + "</div>" + Environment.NewLine + "</td>" + Environment.NewLine;


                //Merged Entity
                relEn = MergedDMEntity.RelatedEntities.FirstOrDefault(f => f.Name == ren.Name);
                tblString += "<td>" + Environment.NewLine + relEn.DisplayName + "<div class=dvActiveTotal>" + relEn.ActiveTotal + "</div>" + Environment.NewLine +
                "<div class='dvInactiveTotal'>" + relEn.InactiveTotal + "</div>" + Environment.NewLine + "</td>" + Environment.NewLine;

                //Duplicate Entities
                foreach (DMEntity dme in DMEntities)
                {
                    if (dme.Id == SourceDM.Id) continue;
                    relEn = dme.RelatedEntities.FirstOrDefault(f => f.Name == ren.Name);
                    if (relEn == null) continue;

                    tblString += "<td>" + Environment.NewLine + relEn.DisplayName + "<div class=dvActiveTotal>" + relEn.ActiveTotal + "</div>" + Environment.NewLine +
                    "<div class='dvInactiveTotal'>" + relEn.InactiveTotal + "</div>" + Environment.NewLine + "</td>" + Environment.NewLine;
                }
                tblString += "</tr>";
            }

            #endregion

            string htmlString = @"<html>
	                                <head>" + Environment.NewLine + css + Environment.NewLine + "</head>" +
                                    Environment.NewLine + @"<body>
	<div id = 'dvConainer'>" + Environment.NewLine + tblString + Environment.NewLine + @"</div>" + Environment.NewLine +
        "</ body >" + Environment.NewLine + "</ html > ";

            return htmlString;
        }

        public async Task CreateSnapshotHtmlAsync()
        {
            string snapshotHtml = GetHtmlString();
            CloudBlobClient client;
            CloudBlobContainer container;

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            client = storageAccount.CreateCloudBlobClient();
            container = client.GetContainerReference("gitis-dupman-snapshots");
            await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

            string bloblName;
            CloudBlockBlob blob;

            bloblName = "Snapshot_" + SourceEntity.Id.ToString() + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".html";
            blob = container.GetBlockBlobReference(bloblName);
            blob.Properties.ContentType = "text/html";

            bool blobExists = await blob.ExistsAsync();

            if (!blobExists)
            {
                byte[] byteArray = Encoding.ASCII.GetBytes(snapshotHtml);
                MemoryStream stream = new MemoryStream(byteArray);

                await blob.UploadFromStreamAsync(stream);
            }
        }

        public ManualRetrieveResponse ConvertToManualMergeResponse()
        {
            ManualRetrieveResponse manualMergeResponse = new ManualRetrieveResponse();
            manualMergeResponse.DMEntities = DMEntities;
            manualMergeResponse.ResponseText = ResponseText;
            manualMergeResponse.Success = Success;

            return manualMergeResponse;
        }

    }

    public class ManualRetrieveResponse
    {
        public List<DMEntity> DMEntities = new List<DMEntity>();
        public string ResponseText { get; set; }
        public bool Success { get; set; }
    }

}
