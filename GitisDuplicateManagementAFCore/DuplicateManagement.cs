using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.IO;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;

namespace GitisDuplicateManagementAFCore
{
    public class DMAttribute
    {
        public string DisplayName { get; set; }
        public string Name { get; set; }
        public DMAttributeType Type { get; set; }
        public object Value { get; set; }
        public string FormattedValue { get; set; }
        public bool IsReadOnly { get; set; }
        public string Lookuplogicalname { get; set; }
        public bool IsDirty { get; set; }
        public bool IsDifferentFromCurrentRecord { get; set; }

    }
    public class DMEntity
    {
        public List<DMAttribute> DMAttributes = new List<DMAttribute>();
        public Guid Id { get; set; }
        public string LogicalName { get; set; }
        public int DuplicateScore { get; set; }
        public List<RelatedEntity> RelatedEntities = new List<RelatedEntity>();
        public bool IncludeInMarge { get; set; }
        public List<MarketingEvent> MarketingEvents = new List<MarketingEvent>();
        public bool IsActive { get; set; }

        public bool IsOwnedByAdviser { get; set; }
        public bool IsAdviserStatusClosed { get; set; }

        MergeConfig mergeConfig;
        public DMEntity()
        {

        }

        public DMEntity(Entity entity, MergeConfig Mergeconfig)
        {
            mergeConfig = Mergeconfig;
            LogicalName = entity.LogicalName;
            Id = entity.Id;

            foreach (DMAttribute dma in mergeConfig.Attributes)
            {
                DMAttribute dmAttribute = new DMAttribute();

                if (!entity.Attributes.Contains(dma.Name))
                {
                    MakeEmptyDMAttribute(dma, dmAttribute);
                    continue;
                }

                var attribute = entity.Attributes.FirstOrDefault(f => f.Key == dma.Name);
                
                
                if (attribute.Key == null) continue;


                dmAttribute.Name = dma.Name;
                dmAttribute.DisplayName = dma.DisplayName;
                dmAttribute.Type = dma.Type;
                dmAttribute.IsReadOnly = dma.IsReadOnly;
                
               if(!Helper.ApplyValueToDmAttribute(dma.Type, dmAttribute, entity, this, attribute))
                { 
                    continue; 
                }

                DMAttributes.Add(dmAttribute);
            }

            // Apply "Apply" Data to DMAttribute
            foreach (DMAttribute dma in mergeConfig.RequiredApplyAttributes)
            {
                DMAttribute dmAttribute = new DMAttribute();

                if (!entity.Attributes.Contains(dma.Name))
                {
                    MakeEmptyDMAttribute(dma, dmAttribute);
                    continue;
                }

                var attribute = entity.Attributes.FirstOrDefault(f => f.Key == dma.Name);


                if (attribute.Key == null) continue;


                dmAttribute.Name = dma.Name;
                dmAttribute.DisplayName = dma.DisplayName;
                dmAttribute.Type = dma.Type;

                // Mark Read Only
                dmAttribute.IsReadOnly = true;

                if (!Helper.ApplyValueToDmAttribute(dma.Type, dmAttribute, entity, this, attribute))
                {
                    continue;
                }

                DMAttributes.Add(dmAttribute);
            }


            //Check if owned by adviser
            Guid ownerId = ((EntityReference)entity["ownerid"]).Id;
            if (mergeConfig.Advisors.Contains(ownerId)) IsOwnedByAdviser = true;
            else IsOwnedByAdviser = false;

            //Check if Candidate Adviser status is closed
            if (entity.Attributes.Contains("dfe_candidateadviserstatus") &&
               ((OptionSetValue)entity["dfe_candidateadviserstatus"]).Value == 222750001) IsAdviserStatusClosed = true;
            else IsAdviserStatusClosed = false;

            void MakeEmptyDMAttribute(DMAttribute dma, DMAttribute dmAttribute)
            {
                dmAttribute.Name = dma.Name;
                dmAttribute.DisplayName = dma.DisplayName;
                dmAttribute.Type = dma.Type;
                dmAttribute.IsReadOnly = dma.IsReadOnly;
                dmAttribute.Value = null;
                dmAttribute.FormattedValue = null;
                DMAttributes.Add(dmAttribute);
            }
        }

        public void GetRelatedEntities(IOrganizationService service)
        {
            foreach (RelatedEntity item in mergeConfig.RelatedEntities)
            {
                RelatedEntity relatedEntity = new RelatedEntity
                {
                    Name = item.Name,
                    RecordName = item.RecordName,
                    FieldName = item.FieldName
                };
                RelatedEntities.Add(relatedEntity);

                if (mergeConfig.LogSnapshot) item.GetData(service, this);
            }
        }

        public void GetMarketingEvents(IOrganizationService service)
        {

            QueryExpression qe = new QueryExpression();
            ConditionExpression ce = new ConditionExpression();
            FilterExpression fe = new FilterExpression();
            OrderExpression oe = new OrderExpression();

            qe = new QueryExpression();
            qe.EntityName = "msevtmgt_eventregistration";
            qe.ColumnSet = new ColumnSet("msevtmgt_name", "msevtmgt_eventid", "msevtmgt_contactid", "createdon");

            ce.AttributeName = "msevtmgt_contactid";
            ce.Operator = ConditionOperator.Equal;
            ce.Values.Add(Id);

            fe.Conditions.Add(ce);
            fe.Conditions.Add(new ConditionExpression("statecode", ConditionOperator.Equal, 0));
            qe.Criteria.AddFilter(fe);

            oe.AttributeName = "createdon";
            oe.OrderType = OrderType.Descending;
            qe.Orders.Add(oe);

            EntityCollection ecol = service.RetrieveMultiple(qe);

            if (ecol.Entities.Count > 0)
            {

                MarketingEvents = new List<MarketingEvent>();

                foreach (Entity entity in ecol.Entities)
                {
                    if (entity.Attributes.Contains("msevtmgt_contactid"))
                    {
                        string contactId = ((EntityReference)entity["msevtmgt_contactid"]).Id.ToString();

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


                            MarketingEvent addedEvent = MarketingEvents.FirstOrDefault(f => f.Id == mEvent.Id);

                            if (addedEvent != null) mEvent = addedEvent;
                            else MarketingEvents.Add(mEvent);

                            eventReg.EventId = mEvent.Id;
                            mEvent.EventRegistrations.Add(eventReg);
                            GetChecIn(mEvent, service);
                        }
                    }
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
    }
    public class RelatedEntity
    {
        public string RecordName { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string FieldName { get; set; }
        public int ActiveTotal { get; set; }
        public int InactiveTotal { get; set; }

        public void GetData(IOrganizationService service, List<DMEntity> DMEntities, ILogger logger)
        {
            logger.LogInformation("Entering: GetData");

            logger.LogInformation("RecordName:{0}, Name:{1}, DisplayName:{2}, FieldName:{3}",RecordName,Name, DisplayName, FieldName);
            try
            {
                QueryExpression qe = new QueryExpression();
                ConditionExpression ce = new ConditionExpression();
                FilterExpression fe = new FilterExpression();
                EntityCollection ecol = null;

                qe = new QueryExpression();
                qe.EntityName = Name;

                logger.LogInformation("Retrieving information for {0}", Name);

                if (Name == "annotation")
                {
                    qe.ColumnSet = new ColumnSet(FieldName, "isdocument");
                }
                else
                {
                    qe.ColumnSet = new ColumnSet(FieldName, "statecode");
                }

                ce.AttributeName = FieldName;
                ce.Operator = ConditionOperator.In;

                foreach (DMEntity item in DMEntities)
                {
                    ce.Values.Add(item.Id);
                }

                fe.Conditions.Add(ce);
                qe.Criteria.AddFilter(fe);

                ecol = service.RetrieveMultiple(qe);

                if (ecol.Entities.Count > 0)
                {
                    logger.LogInformation("Found {0} children for parent field Name {1}", ecol.Entities.Count, FieldName);
                    foreach (Entity entity in ecol.Entities)
                    {
                        Guid parentId = ((EntityReference)entity[FieldName]).Id;
                        DMEntity parentDmEntity = DMEntities.FirstOrDefault(f => f.Id == parentId);

                        if (parentDmEntity != null)
                        {
                            RelatedEntity relEntity = parentDmEntity.RelatedEntities.FirstOrDefault(f => f.Name == Name);

                            if (relEntity.Name == "annotation") relEntity.ActiveTotal += 1;
                            else
                            {
                                if (((OptionSetValue)entity["statecode"]).Value == 0) relEntity.ActiveTotal += 1;
                                else relEntity.InactiveTotal += 1;
                            }
                        }
                    }
                }


            }
            catch (Exception exc)
            {

                logger.LogError(exc,"Error in Get Related Entity Data");
            }

        }

        public void GetData(IOrganizationService service, DMEntity dmEntity)
        {
            try
            {
                QueryExpression qe = new QueryExpression();
                ConditionExpression ce = new ConditionExpression();
                FilterExpression fe = new FilterExpression();
                EntityCollection ecol = null;

                qe = new QueryExpression();
                qe.EntityName = Name;

                if (Name == "annotation")
                {
                    qe.ColumnSet = new ColumnSet(FieldName, "isdocument");
                }
                else
                {
                    qe.ColumnSet = new ColumnSet(FieldName, "statecode");
                }

                ce.AttributeName = FieldName;
                ce.Operator = ConditionOperator.Equal;
                ce.Values.Add(dmEntity.Id);

                fe.Conditions.Add(ce);
                qe.Criteria.AddFilter(fe);

                ecol = service.RetrieveMultiple(qe);

                if (ecol.Entities.Count > 0)
                {
                    foreach (Entity entity in ecol.Entities)
                    {
                        Guid parentId = ((EntityReference)entity[FieldName]).Id;
                        RelatedEntity relEntity = dmEntity.RelatedEntities.FirstOrDefault(f => f.Name == Name);

                        if (relEntity.Name == "annotation") relEntity.ActiveTotal += 1;
                        else
                        {
                            if (((OptionSetValue)entity["statecode"]).Value == 0) relEntity.ActiveTotal += 1;
                            else relEntity.InactiveTotal += 1;
                        }
                    }
                }


            }
            catch (Exception)
            {


            }

        }
    }
    public class MarketingEvent
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string LogicanName { get; set; }
        public string DisplayName { get; set; }
        public string ContactId { get; set; }
        public int ActiveTotal { get; set; }
        public int InactiveTotal { get; set; }
        public bool CheckedIn { get; set; }
        public List<EventRegistration> EventRegistrations { get; set; }


    }
    public class EventRegistration
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string EventId { get; set; }
        public string ContactId { get; set; }
        public bool CheckedIn { get; set; }

    }
    public class GitisServiceSubscription
    {
        //TTAService
        public int TTAServiceSubscriptionChannel { get; set; }
        public bool TTAServiceIsSubscriber { get; set; }
        public DateTime TTAServiceStartDate { get; set; }
        public bool TTAServiceDoNotEmail { get; set; }
        public bool TTAServiceDoNotBulkEmail { get; set; }
        public bool TTAServiceDoNotPostalMail { get; set; }
        public bool TTAServiceDoNotBulkPostalMail { get; set; }
        public bool TTAServiceDoNotSendMM { get; set; }

        //MailingListServicer
        public int MailingListServiceSubscriptionChannel { get; set; }
        public bool MailingListServiceIsSubscriber { get; set; }
        public DateTime MailingListServiceStartDate { get; set; }
        public bool MailingListServiceDoNotEmail { get; set; }
        public bool MailingListServiceDoNotBulkEmail { get; set; }
        public bool MailingListServiceDoNotPostalMail { get; set; }
        public bool MailingListServiceDoNotBulkPostalMail { get; set; }
        public bool MailingListServiceDoNotSendMM { get; set; }



        public int EventsServiceSubscriptionType { get; set; }




    }
    public class ITTAppliedTo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public Guid ContactId { get; set; }
        public DateTime DateApplied { get; set; }
        public DateTime DateAccepted { get; set; }

        public ITTAppliedTo(Entity ittAppliedTo)
        {
            Id = ittAppliedTo.Id;
            if (ittAppliedTo.Attributes.Contains("dfe_name")) Name = ittAppliedTo["dfe_name"].ToString();
            if (ittAppliedTo.Attributes.Contains("dfe_contactid")) ContactId = ((EntityReference)ittAppliedTo["dfe_contactid"]).Id;
            if (ittAppliedTo.Attributes.Contains("dfe_dateapplied")) DateApplied = (DateTime)ittAppliedTo["dfe_dateapplied"];
            if (ittAppliedTo.Attributes.Contains("dfe_dateaccepted")) DateAccepted = (DateTime)ittAppliedTo["dfe_dateaccepted"];

        }

    }

    public class AdviserToDoItem
    {
        public Guid ID { get; set; }
        public Guid CandidateId { get; set; }
        public Guid ModifiedByName { get; set; }
        public DateTime MofieidOn { get; set; }
        public DateTime CreatedOn { get; set; }

        OptionSetValue DfeStatus { get; set; }
        OptionSetValue StatusReason { get; set; }
        OptionSetValue StateCode { get; set; }



    }

    public enum MergeOption
    {
        Merge_Default,
        Merge_DoNotOverwriteMaster,
        Merge_ITTAppliedTo,
        Skip_MasterRecordClosed,
        Skip_MultipleOwners,
        Skip_ITTAppliedTo,
        SKIP_NoDuplicates,
        SKIP_DuplicateIsSubscriber,
        SKIP_MultipleOwners_Notified
    }
    public enum MergeStatus
    {
        SUCCESS,
        FAIL,
        SKIP_ErrorOccured,
        SKIP_MasterRecordClosed,
        SKIP_MultipleOwners,
        SKIP_AlreadyMerged,
        SKIP_InactiveRecord,
        SKIP_NoDuplicates,
        SKIP_ITTAppliedTo,
        SKIP_DuplicateIsSubscriber,
        SKIP_MultipleOwners_Notified



    }
    public enum DMAttributeType
    {
        Picklist,
        DateTime,
        String,
        Uniqueidentifier,
        Boolean,
        Double,
        Integer,
        Lookup,
        Memo,
        EntityName,
        BigInt,
        Money,
        Decimal,
        Customer,
        Virtual,
        State,
        Status,
        Owner
    }


}
