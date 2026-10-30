using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagementAFCore
{
    public static class CrmConnectionHelper
    {
        public static IOrganizationService GetIOrganizationService()
        {
            try
            {
                string crmUrl = Environment.GetEnvironmentVariable(C.SERVICE_URL);
                string crmAppId = Environment.GetEnvironmentVariable(C.APPID);
                string crmClientSecret = Environment.GetEnvironmentVariable(C.CLIENT_SECRET);

                string connectionString = $"AuthType=ClientSecret;url={crmUrl};ClientId={crmAppId};ClientSecret={crmClientSecret}";

                var client = new ServiceClient(connectionString);
                return client;
            }
            catch (Exception exc)
            {
                return null;
            }
        }
    }
}
