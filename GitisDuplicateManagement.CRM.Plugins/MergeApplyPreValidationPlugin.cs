using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitisDuplicateManagement.CRM.Plugins
{
    public class MergeApplyPreValidationPlugin : IPlugin
    {
        private const string _DFE_APPLYID = "dfe_applyid";
        private const string _IMAGE = "image";
        private const string _SUB_IMAGE = "subimage";

        public void Execute(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
                throw new ArgumentNullException("serviceProvider");

            // Obtain the execution context service from the service provider.
            var PluginExecutionContext = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            // Obtain the Organization Service factory service from the service provider
            var ServiceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            // Use the factory to generate the Organization Service.
            var OrganizationService = ServiceFactory.CreateOrganizationService(null);

            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            if (!string.Equals(PluginExecutionContext.MessageName.ToLower(), "merge", StringComparison.InvariantCultureIgnoreCase))
            {
                return;
            }

            // Retrieve the image and subimage from PreEntityImages
            if (PluginExecutionContext.PreEntityImages.Contains(_IMAGE) && PluginExecutionContext.PreEntityImages.Contains(_SUB_IMAGE))
            {
                var image = PluginExecutionContext.PreEntityImages[_IMAGE];
                var subImage = PluginExecutionContext.PreEntityImages[_SUB_IMAGE];

                // only consider the scenario where both records have a value within the DFE APPLY ID field
                if (subImage.Contains(_DFE_APPLYID) && image.Contains(_DFE_APPLYID))
                {
                    if (subImage.GetAttributeValue<string>(_DFE_APPLYID)
                        .Trim()
                        .ToLower()
                        .CompareTo(image.GetAttributeValue<string>(_DFE_APPLYID).Trim().ToLower()) != 0)
                    {
                        throw new Exception("Cannot merge records that do not have the same apply id!");
                    }
                }
            }
        }
    }
}
