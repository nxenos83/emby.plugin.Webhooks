using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Emby.Webhooks
{
    public class envelope
    {

        public string @event { get; set; }
        public bool user { get; set; }
        public bool owner { get; set; }
        public Account Account { get; set; }
        public Server Server { get; set; }
        public Player Player { get; set; }
        public Metadata Metadata { get; set; }

    }



    public class Account
    {
        public string id { get; set; }
        public string thumb { get; set; }
        public string title { get; set; }
    }

    public class Server
    {
        public string title { get; set; }
        public string uuid { get; set; }
    }

    public class Player
    {
        public bool local { get; set; }
        public string publicAddress { get; set; }
        public string title { get; set; }
        public string uuid { get; set; }
    }

    public class Metadata
    {
        public string librarySectionType { get; set; }
        public string ratingKey { get; set; }
        public string key { get; set; }
        public string parentRatingKey { get; set; }
        public string grandparentRatingKey { get; set; }
        public string guid { get; set; }
        public int librarySectionID { get; set; }
        public string type { get; set; }
        public string title { get; set; }
        public string grandparentKey { get; set; }
        public string parentKey { get; set; }
        public string grandparentTitle { get; set; }
        public string parentTitle { get; set; }
        public string summary { get; set; }
        public int index { get; set; }
        public int parentIndex { get; set; }
        public int ratingCount { get; set; }
        public string thumb { get; set; }
        public string art { get; set; }
        public string parentThumb { get; set; }
        public string grandparentThumb { get; set; }
        public string grandparentArt { get; set; }
        public int addedAt { get; set; }
        public int updatedAt { get; set; }
    }

}
