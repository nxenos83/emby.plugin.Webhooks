using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;

namespace Emby.Webhooks
{
    public class Plugin : MediaBrowser.Common.Plugins.BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name
        {
            get
            {
                return "Webhooks";
            }
        }

        public override string Description
        {
            get
            {
                return "Webhook handler for Emby";
            }
        }

        public static Plugin Instance { get; private set; }

        private Guid _id = new Guid("fda427d3-cb73-4b3f-8e11-c67a61f7a8ed");
        public override Guid Id
        {
            get { return _id; }
        }


        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
                }

            };
        }
    }
}
