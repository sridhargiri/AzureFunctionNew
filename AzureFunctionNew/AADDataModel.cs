using Microsoft.Graph;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace AzureFunctionNew
{
    public class AADDataModel
    {

    }

    public class MetaData
    {
        [JsonProperty]
        public string CREATION_ID { get; set; }
        [JsonProperty]
        public DateTime CREATION_DATETIME { get; set; }
        [JsonProperty]
        public string LAST_MODIFIED_ID { get; set; }
        [JsonProperty]
        public DateTime LAST_MODIFIED_DATETIME { get; set; }

    }
    public class AADGroup : MetaData
    {
        [JsonProperty]
        public string Id;

        [JsonProperty]
        public string DisplayName;


        public AADGroup(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

    }

    public class AADGroupMember : MetaData
    {
        [JsonProperty]
        public string groupId;

        [JsonProperty]
        public string groupName;

        [JsonProperty]
        public string id;

        [JsonProperty]
        public string memberType;

        public AADGroupMember(Group group, string id, string memberType)
        {
            this.groupId = group.Id;
            this.groupName = group.DisplayName;
            this.id = id;
            this.memberType = memberType;
        }
    }

    public class AADUser : AADGroupMember
    {
        [JsonProperty]
        public string displayName;

        [JsonProperty]
        public string userPrincipalName;

        public AADUser(Group group, string id, string memberType, string displayName, string userPrincipalName) : base(group, id, "User")
        {
            this.displayName = displayName;
            this.userPrincipalName = userPrincipalName;
        }

    }

    public class AADServicePrincipal : AADGroupMember
    {
        [JsonProperty]
        public string displayName;

        [JsonProperty]
        public string applicationId;

        public AADServicePrincipal(Group group, string id, string memberType, string displayName, string applicationId) : base(group, id, "SPN")
        {
            this.displayName = displayName;
            this.applicationId = applicationId;
        }

    }
}
