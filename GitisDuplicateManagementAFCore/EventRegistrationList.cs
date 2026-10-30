using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{
    public class EventRegistrationList
    {
        public List<MarketingEvent> marketingEvents = new List<MarketingEvent>();
        public Guid contactId { get; set; }
        IOrganizationService service;

        public EventRegistrationList(IOrganizationService Service, string ContactID)
        {
            service = Service;
            contactId = new Guid(ContactID);
        }

        public void GetEventRegistrations()
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
            ce.Values.Add(contactId);

            fe.Conditions.Add(ce);
            fe.Conditions.Add(new ConditionExpression("statecode", ConditionOperator.Equal, 0));
            qe.Criteria.AddFilter(fe);

            oe.AttributeName = "createdon";
            oe.OrderType = OrderType.Descending;
            qe.Orders.Add(oe);

            EntityCollection ecol = service.RetrieveMultiple(qe);

            if (ecol.Entities.Count > 0)
            {

                foreach (Entity entity in ecol.Entities)
                {

                    if (entity.Attributes.Contains("msevtmgt_eventid"))
                    {
                        MarketingEvent mEvent = new MarketingEvent();
                        mEvent.EventRegistrations = new List<EventRegistration>();

                        EntityReference erMarketingEvent = (EntityReference)entity["msevtmgt_eventid"];
                        mEvent.Id = erMarketingEvent.Id.ToString();
                        mEvent.Name = erMarketingEvent.Name;
                        mEvent.ContactId = contactId.ToString();

                        EventRegistration eventReg = new EventRegistration();
                        if (entity.Attributes.Contains("msevtmgt_name")) eventReg.Name = entity["msevtmgt_name"].ToString();
                        else eventReg.Name = "";

                        eventReg.Id = entity.Id.ToString();
                        eventReg.ContactId = contactId.ToString();

                        MarketingEvent addedEvent = marketingEvents.FirstOrDefault(f => f.Id == mEvent.Id);

                        if (addedEvent != null) mEvent = addedEvent;
                        else marketingEvents.Add(mEvent);

                        eventReg.EventId = mEvent.Id;
                        mEvent.EventRegistrations.Add(eventReg);
                        GetChecIn(mEvent, service);


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

        public void Merge()
        {
            Entity entity = new Entity("msevtmgt_eventregistration");
            entity.Attributes.Add("statecode", new OptionSetValue(1));
            entity.Attributes.Add("statuscode", new OptionSetValue(2));

            if (marketingEvents.Count > 0)
            {
                foreach (MarketingEvent mEvent in marketingEvents)
                {
                    if (mEvent.EventRegistrations != null)
                    {
                        if (mEvent.EventRegistrations.Count > 1)
                        {
                            EventRegistration exEveReg = null;

                            foreach (EventRegistration evReg in mEvent.EventRegistrations)
                            {
                                if (exEveReg == null)
                                {
                                    exEveReg = evReg;
                                    continue;
                                }

                                if (evReg.CheckedIn == true && exEveReg.CheckedIn == false)
                                {
                                    entity.Id = new Guid(exEveReg.Id);
                                    service.Update(entity);
                                    exEveReg = evReg;
                                }
                                else
                                {
                                    entity.Id = new Guid(evReg.Id);
                                    service.Update(entity);
                                }

                            }
                        }
                    }
                }
            }
        }
    }
}
