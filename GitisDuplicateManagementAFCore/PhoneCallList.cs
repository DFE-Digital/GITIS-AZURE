using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{
    public class PhoneCallList
    {
        public List<PhoneCall> phoneCalls = new List<PhoneCall>();
        public Guid contactId { get; set; }

        IOrganizationService service;

        public PhoneCallList(IOrganizationService _Service, string ContactId)
        {
            service = _Service;
            contactId = new Guid(ContactId);
        }

        public void GetPhoneCalls()
        {

            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                                      <entity name='phonecall'>
                                        <attribute name='subject' />
                                        <attribute name='statuscode' />
                                        <attribute name='prioritycode' />
                                        <attribute name='scheduledstart' />
                                        <attribute name='dfe_callweighting' />
                                        <attribute name='createdon' />
                                        <attribute name='regardingobjectid' />
                                        <attribute name='activityid' />
                                        <attribute name='dfe_tocontactguid' />
                                        <attribute name='dfe_contactid' />
                                        <attribute name='to' />
                                        <attribute name='from' />
                                        <attribute name='directioncode' />
                                        <order attribute='subject' descending='false' />
                                        <filter type='and'>
                                          <filter type='or'>
                                            <condition attribute='dfe_tocontactguid' operator='eq' value='" + contactId.ToString() + @"' />
                                            <condition attribute='dfe_contactid' operator='eq' uitype='contact' value='{" + contactId.ToString() + @"}' />
                                          </filter>
                                          <condition attribute='statuscode' operator='eq' value='1' />
                                        </filter>
                                      </entity>
                                    </fetch>";

            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));
            if (eCol == null) return;
            if (eCol.Entities.Count > 0)
            {
                foreach (Entity entity in eCol.Entities)
                {
                    PhoneCall pc = new PhoneCall(entity.Id);
                    pc.createdOn = (DateTime)entity["createdon"];
                    if (entity.Attributes.Contains("scheduledstart")) pc.scheduleStart = (DateTime)entity["scheduledstart"];
                    if (entity.Attributes.Contains("dfe_callweighting")) pc.callWeighting = (int)entity["dfe_callweighting"];

                    phoneCalls.Add(pc);
                }
            }
        }

        public void Merge()
        {
            if (phoneCalls.Count == 0) return;
            PhoneCall xPC = null;

            foreach (PhoneCall pc in phoneCalls)
            {
                if (xPC == null)
                {
                    xPC = pc;
                    continue;
                }
                else
                {
                    if (pc.callWeighting > xPC.callWeighting) xPC.deActivate = true;
                    else if (pc.callWeighting == xPC.callWeighting)
                    {
                        if (pc.scheduleStart > xPC.scheduleStart) xPC.deActivate = true;
                        else if (pc.scheduleStart == xPC.scheduleStart)
                        {
                            if (pc.createdOn > xPC.createdOn) xPC.deActivate = true;
                            else pc.deActivate = true;
                        }
                        else pc.deActivate = true;
                    }
                    else pc.deActivate = true;
                }

                Entity entity = new Entity();
                entity.LogicalName = "phonecall";
                if (pc.deActivate == true) entity.Id = pc.id;
                else if (xPC.deActivate == true) entity.Id = xPC.id;

                entity.Attributes.Add("statecode", new OptionSetValue(2));
                entity.Attributes.Add("statuscode", new OptionSetValue(3));
                service.Update(entity);

            }
        }
    }

    public class PhoneCall
    {
        public Guid id { get; set; }
        public int callWeighting { get; set; }
        public DateTime createdOn { get; set; }
        public DateTime scheduleStart { get; set; }
        public bool deActivate = false;

        public PhoneCall(Guid guid)
        {
            id = guid;

        }

    }
}
