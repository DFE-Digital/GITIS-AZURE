using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{
    public class CandidateStepList
    {
        public List<CandidateStep> candidateSteps = new List<CandidateStep>();
        public Guid contactId { get; set; }


        IOrganizationService service;

        public CandidateStepList(IOrganizationService _Service, string ContactId)
        {
            service = _Service;
            contactId = new Guid(ContactId);
        }

        public void GetCandidateSteps()
        {
            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                                  <entity name='dfe_candidatejourneystep'>
                                    <attribute name='dfe_candidatejourneystepid' />
                                    <attribute name='dfe_name' />
                                    <attribute name='createdon' />
                                    <attribute name='statuscode' />
                                    <attribute name='statecode' />
                                    <order attribute='dfe_name' descending='false' />
                                    <filter type='and'>
                                      <condition attribute='dfe_contact' operator='eq' uitype='contact' value='{" + contactId.ToString() + @"}' />
                                      <condition attribute='statecode' operator='eq' value='0' />  
                                    </filter>
                                  </entity>
                                </fetch>";

            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));
            if (eCol == null) return;
            if (eCol.Entities.Count > 0)
            {
                foreach (Entity entity in eCol.Entities)
                {
                    CandidateStep canStep = new CandidateStep(entity.Id);
                    canStep.createdOn = (DateTime)entity["createdon"];

                    if (entity.Attributes.Contains("dfe_name")) canStep.name = entity["dfe_name"].ToString();
                    if (entity.Attributes.Contains("statuscode")) canStep.statusReason = (CandidateStep.StatusReason)((OptionSetValue)entity["statuscode"]).Value;

                    candidateSteps.Add(canStep);
                }
            }
        }

        public void Merge()
        {
            if (candidateSteps.Count == 0) return;

            List<CandidateStep> masterList = new List<CandidateStep>();

            foreach (CandidateStep cs in candidateSteps)
            {
                CandidateStep masterCS = masterList.FirstOrDefault(f => f.name == cs.name);
                if (masterCS == null)
                {
                    masterList.Add(cs);
                    continue;
                }
                else
                {
                    if (cs.statusReason > masterCS.statusReason)
                    {
                        masterCS.deActivate = true;
                    }
                    else if (cs.createdOn > masterCS.createdOn && cs.statusReason == masterCS.statusReason)
                    {
                        masterCS.deActivate = true;
                    }
                    else
                    {
                        cs.deActivate = true;
                    }
                }

                Entity entity = new Entity();
                entity.LogicalName = "dfe_candidatejourneystep";

                if (cs.deActivate == true) entity.Id = cs.id;
                else
                {
                    entity.Id = masterCS.id;
                    masterList.Add(cs);
                    masterList.Remove(masterCS);
                }

                entity.Attributes.Add("statecode", new OptionSetValue(1));
                entity.Attributes.Add("statuscode", new OptionSetValue(2));

                service.Update(entity);
            }
        }
    }
    public class CandidateStep
    {
        public Guid id { get; set; }
        public StatusReason statusReason { get; set; }
        public string name { get; set; }
        public DateTime createdOn { get; set; }

        public bool deActivate = false;
        public CandidateStep(Guid guid)
        {
            id = guid;
        }

        public enum StatusReason
        {
            NotStarted = 1,
            NotRequired = 222750000,
            InProgress = 222750001,
            Done = 222750002
        }
    }
}
