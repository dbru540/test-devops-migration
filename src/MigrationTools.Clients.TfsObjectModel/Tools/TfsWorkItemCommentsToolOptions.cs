using Newtonsoft.Json;
using MigrationTools.Tools.Infrastructure;

namespace MigrationTools.Tools
{
    public class TfsWorkItemCommentsToolOptions : ToolOptions
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IgnoreMigrationGeneratedComments { get; set; } = true;
    }
}
