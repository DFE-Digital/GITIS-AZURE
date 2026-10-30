using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{
    public class CandidatePrivacyPolicyList
    {
        public List<CandidatePrivacyPolicy> candidatePrivacyPolicies = new List<CandidatePrivacyPolicy>();
        public Guid contactId { get; set; }

        IOrganizationService service;

        public CandidatePrivacyPolicyList(IOrganizationService _Service, string ContactId)
        {
            service = _Service;
            contactId = new Guid(ContactId);
        }

        public void GetCandidatePrivacyPolicies()
        {
            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                                      <entity name='dfe_candidateprivacypolicy'>
                                        <attribute name='dfe_candidateprivacypolicyid' />
                                        <attribute name='dfe_name' />
                                        <attribute name='createdon' />
                                        <attribute name='dfe_timeofconsent' />
                                        <attribute name='statuscode' />
                                        <attribute name='statecode' />
                                        <attribute name='dfe_candidate' />
                                        <attribute name='dfe_privacypolicynumber' />
                                        <order attribute='dfe_name' descending='false' />
                                        <filter type='and'>
                                          <condition attribute='statecode' operator='eq' value='0' />
                                          <condition attribute='dfe_candidate' operator='eq' uitype='contact' value='{" + contactId.ToString() + @"}' />
                                        </filter>
                                      </entity>
                                    </fetch>";

            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));
            if (eCol == null) return;
            if (eCol.Entities.Count > 0)
            {
                foreach (Entity entity in eCol.Entities)
                {
                    CandidatePrivacyPolicy cpp = new CandidatePrivacyPolicy(entity.Id);
                    cpp.createdOn = (DateTime)entity["createdon"];
                    if (entity.Attributes.Contains("dfe_privacypolicynumber"))
                    {
                        cpp.policyNumber = ((EntityReference)entity["dfe_privacypolicynumber"]).Name;

                    }

                    candidatePrivacyPolicies.Add(cpp);
                }
            }
        }

        public void Merge()
        {
            if (candidatePrivacyPolicies.Count == 0) return;

            List<CandidatePrivacyPolicy> masterList = new List<CandidatePrivacyPolicy>();

            foreach (CandidatePrivacyPolicy cpp in candidatePrivacyPolicies)
            {
                CandidatePrivacyPolicy masterCpp = candidatePrivacyPolicies.FirstOrDefault(f => f.policyNumber == cpp.policyNumber);

                if (masterCpp == null)
                {
                    masterList.Add(cpp);
                    continue;
                }
                else
                {
                    if (cpp.createdOn > masterCpp.createdOn) masterCpp.deActivate = true;
                    else cpp.deActivate = true;
                }

                Entity entity = new Entity();
                entity.LogicalName = "dfe_candidateprivacypolicy";

                if (cpp.deActivate == true) entity.Id = cpp.id;
                else
                {
                    entity.Id = masterCpp.id;
                    masterList.Add(cpp);
                    masterList.Remove(masterCpp);
                }

                entity.Attributes.Add("statecode", new OptionSetValue(1));
                entity.Attributes.Add("statuscode", new OptionSetValue(2));

                service.Update(entity);

            }
        }
    }

    public class CandidatePrivacyPolicy
    {
        public Guid id { get; set; }
        public string policyNumber { get; set; }
        public DateTime createdOn { get; set; }

        public bool deActivate = false;

        public CandidatePrivacyPolicy(Guid guid)
        {
            id = guid;

        }

    }
}
