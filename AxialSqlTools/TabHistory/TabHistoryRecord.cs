using Newtonsoft.Json;
using System;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    public enum TabHistoryEventType
    {
        Opened,
        Activated,
        Closed,
        Executed
    }

    public class TabHistoryRecord
    {
        [JsonIgnore]
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public TabHistoryEventType EventType { get; set; }
        public string DocumentName { get; set; }
        public string DocumentPath { get; set; }
        public string DataSource { get; set; }
        public string DatabaseName { get; set; }
        public string Content { get; set; }
        public string ContentHash { get; set; }
        public int CharCount { get; set; }
        [JsonIgnore]
        public string ContentShort { get; set; }

        [JsonIgnore]
        public string EventTypeDisplay
        {
            get
            {
                switch (EventType)
                {
                    case TabHistoryEventType.Opened:
                        return Strings.Get("TabHistory_EventOpened");
                    case TabHistoryEventType.Activated:
                        return Strings.Get("TabHistory_EventActivated");
                    case TabHistoryEventType.Closed:
                        return Strings.Get("TabHistory_EventClosed");
                    case TabHistoryEventType.Executed:
                        return Strings.Get("TabHistory_EventExecuted");
                    default:
                        return EventType.ToString();
                }
            }
        }

        [JsonIgnore]
        public string ContentDisplay => Content ?? Strings.Get("TabHistory_ContentUnchanged");
    }
}
