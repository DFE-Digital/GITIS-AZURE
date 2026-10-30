using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{
    internal class Helper
    {

        public static HttpResponseMessage CreateHttpResponseMessage<T>(HttpStatusCode code, T content)
        {
            HttpResponseMessage toReturn = new HttpResponseMessage(code);
            if (content is string contentAsString)
            {
                toReturn.Content = new StringContent(contentAsString);
            }
            else
            {
                if (content != null)
                {
                    toReturn.Content = new StringContent(JsonConvert.SerializeObject(content));
                }
            }

            return toReturn;
        }

        internal static bool ApplyValueToDmAttribute(
            DMAttributeType typeOfAttribute, 
            DMAttribute newDmAttribute, 
            Entity entity, 
            DMEntity dmEntity, 
            KeyValuePair<string,object> attribute)
        {
            if (typeOfAttribute == DMAttributeType.Uniqueidentifier)
            {
                Guid contactid = (Guid)attribute.Value;
                newDmAttribute.Value = contactid;
                newDmAttribute.FormattedValue = attribute.Value.ToString();

            }
            else if (typeOfAttribute == DMAttributeType.Lookup || typeOfAttribute == DMAttributeType.Owner)
            {
                EntityReference er = (EntityReference)attribute.Value;
                newDmAttribute.Value = er.Id;
                newDmAttribute.FormattedValue = er.Name;
                newDmAttribute.Lookuplogicalname = er.LogicalName;
            }
            else if (typeOfAttribute == DMAttributeType.Picklist || typeOfAttribute == DMAttributeType.State)
            {
                OptionSetValue opValue = (OptionSetValue)attribute.Value;
                newDmAttribute.Value = opValue.Value;
                if (entity.FormattedValues.ContainsKey(newDmAttribute.Name))
                {
                    newDmAttribute.FormattedValue = entity.FormattedValues[newDmAttribute.Name];
                }

                if (dmEntity != null && (newDmAttribute.Name == "statecode" && newDmAttribute.FormattedValue == "Active"))
                {
                    dmEntity.IsActive = true;
                }
                else 
                {
                    if (dmEntity != null)
                    {
                        dmEntity.IsActive = false;
                    }
                }
            }
            else if (typeOfAttribute == DMAttributeType.Boolean)
            {
                newDmAttribute.Value = (bool)attribute.Value;
                newDmAttribute.FormattedValue = entity.FormattedValues[newDmAttribute.Name];

            }
            else if (typeOfAttribute == DMAttributeType.Customer)
            {
                EntityReference er = (EntityReference)attribute.Value;
                newDmAttribute.Value = er.Id;
                newDmAttribute.FormattedValue = er.Name;
            }
            else if (typeOfAttribute == DMAttributeType.DateTime)
            {
                newDmAttribute.Value = (DateTime)attribute.Value;
                newDmAttribute.FormattedValue = ((DateTime)attribute.Value).ToString("dd-MM-yyyy HH:mm:ss");
                if (newDmAttribute.Name == "birthdate") newDmAttribute.FormattedValue = ((DateTime)attribute.Value).ToString("dd-MM-yyyy");
            }
            else if (typeOfAttribute == DMAttributeType.Money)
            {
                newDmAttribute.Value = (Money)attribute.Value;
                newDmAttribute.FormattedValue = newDmAttribute.Value.ToString();
            }
            else if (typeOfAttribute == DMAttributeType.BigInt || typeOfAttribute == DMAttributeType.Decimal || typeOfAttribute == DMAttributeType.Double
                    || typeOfAttribute == DMAttributeType.Integer || typeOfAttribute == DMAttributeType.Memo || typeOfAttribute == DMAttributeType.String)
            {
                newDmAttribute.Value = attribute.Value;
                newDmAttribute.FormattedValue = attribute.Value.ToString();
                if (newDmAttribute.Name == "dfe_duplicatescorecalculated" && dmEntity != null)
                {
                    dmEntity.DuplicateScore = (int)newDmAttribute.Value;
                }
            } else
            {
                return false;
            }

            return true;
        }

        public static DMAttribute CloneDmAttribute(DMAttribute attr)
        {
            DMAttribute applyAttribute = new DMAttribute();
            applyAttribute.Name = attr.Name;
            applyAttribute.Type = attr.Type;
            applyAttribute.IsReadOnly = attr.IsReadOnly;
            applyAttribute.DisplayName = attr.DisplayName;
            applyAttribute.Lookuplogicalname = attr.Lookuplogicalname;
            applyAttribute.IsDifferentFromCurrentRecord = attr.IsDifferentFromCurrentRecord;
            return applyAttribute;
        }
    }
}
