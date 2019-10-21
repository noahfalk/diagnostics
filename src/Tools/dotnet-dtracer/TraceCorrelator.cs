using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.DTracer
{
    public class TraceCorrelator
    {
        Dictionary<string, ActivityTreeItem> _openActivities = new Dictionary<string, ActivityTreeItem>();

        public void Start()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    TimeSpan lag = TimeSpan.FromSeconds(1);
                    await Task.Delay(500);
                    lock (this)
                    {
                        DateTime now = DateTime.UtcNow;
                        foreach (ActivityTreeItem item in _openActivities.Values.ToArray())
                        {
                            if (item.Stopped && now - (item.StartTime + item.Duration) > lag)
                            {
                                _openActivities.Remove(item.Id);
                                if (item.ParentId == null)
                                {
                                    Console.WriteLine(item);
                                }
                            }
                        }
                    }
                }
            });
        }

        public void AddEvent(TraceEvent e)
        {
            lock (this)
            {
                //Console.WriteLine(e.EventName);
                if (e.EventName == "Activity1Start/Start")
                {
                    string listenerEventName = (string)e.PayloadByName("EventName");
                    if (listenerEventName == "Microsoft.AspNetCore.Hosting.BeginRequest")
                    {
                        if (e.PayloadByName("Arguments") is IDictionary<string, object>[] arguments)
                        {
                            string activityId = null;
                            string parentId = null;
                            string operationName = null;
                            string httpMethod = null;
                            string host = null;
                            string path = null;
                            DateTime startTime = default;
                            foreach (var arg in arguments)
                            {
                                string key = (string)arg["Key"];
                                string value = (string)arg["Value"];
                                if (key == "ActivityId") activityId = value;
                                else if (key == "ActivityParentId") parentId = value;
                                else if (key == "ActivityOperationName") operationName = value;
                                else if (key == "Method") httpMethod = value;
                                else if (key == "Host") host = value;
                                else if (key == "Path") path = value;
                                else if (key == "ActivityStartTime") startTime = new DateTime(long.Parse(value));


                                //Console.WriteLine($"{key}: {value}");
                            }
                            string description = $"{httpMethod} {host} {path}";
                            ActivityTreeItem item = new ActivityTreeItem(activityId, parentId, description, startTime);
                            OnStartActivity(item);
                        }
                    }
                }
                else if (e.EventName == "Activity1Stop/Stop")
                {
                    string listenerEventName = (string)e.PayloadByName("EventName");
                    if (listenerEventName == "Microsoft.AspNetCore.Hosting.EndRequest")
                    {
                        if (e.PayloadByName("Arguments") is IDictionary<string, object>[] arguments)
                        {
                            string activityId = null;
                            TimeSpan duration = default;
                            foreach (var arg in arguments)
                            {
                                string key = (string)arg["Key"];
                                string value = (string)arg["Value"];
                                if (key == "ActivityId") activityId = value;
                                else if (key == "ActivityDuration") duration = new TimeSpan(long.Parse(value));
                                //Console.WriteLine($"{arg["Key"]}: {arg["Value"]}");
                            }

                            if(_openActivities.TryGetValue(activityId, out ActivityTreeItem item))
                            {
                                OnStopActivity(item, duration);
                            }
                        }
                    }
                }
                if (e.EventName == "Activity2Start/Start")
                {
                    string listenerEventName = (string)e.PayloadByName("EventName");
                    if (listenerEventName == "System.Net.Http.HttpRequestOut.Start")
                    {
                        if (e.PayloadByName("Arguments") is IDictionary<string, object>[] arguments)
                        {
                            string activityId = null;
                            string parentId = null;
                            DateTime startTime = default;
                            foreach (var arg in arguments)
                            {
                                string key = (string)arg["Key"];
                                string value = (string)arg["Value"];
                                if (key == "ActivityId") activityId = value;
                                else if (key == "ActivityParentId") parentId = value;
                                else if (key == "ActivityStartTime") startTime = new DateTime(long.Parse(value));

                                //Console.WriteLine($"{key}: {value}");
                            }
                            string description = $"HttpClient";
                            ActivityTreeItem item = new ActivityTreeItem(activityId, parentId, description, startTime);
                            OnStartActivity(item);
                        }
                    }
                }
                else if (e.EventName == "Activity2Stop/Stop")
                {
                    string listenerEventName = (string)e.PayloadByName("EventName");
                    if (listenerEventName == "System.Net.Http.HttpRequestOut.Stop")
                    {
                        if (e.PayloadByName("Arguments") is IDictionary<string, object>[] arguments)
                        {
                            string activityId = null;
                            TimeSpan duration = default;
                            foreach (var arg in arguments)
                            {
                                string key = (string)arg["Key"];
                                string value = (string)arg["Value"];
                                if (key == "ActivityId") activityId = value;
                                else if (key == "ActivityDuration") duration = new TimeSpan(long.Parse(value));
                                //Console.WriteLine($"{arg["Key"]}: {arg["Value"]}");
                            }

                            if (_openActivities.TryGetValue(activityId, out ActivityTreeItem item))
                            {
                                OnStopActivity(item, duration);
                            }
                        }
                    }
                }
            }
        }

        private void OnStartActivity(ActivityTreeItem item)
        {
            if (_openActivities.TryGetValue(item.Id, out ActivityTreeItem existingItem))
            {
                existingItem.Description = item.Description;
                existingItem.ParentId = item.ParentId;
                existingItem.StartTime = item.StartTime;
            }
            else
            {
                _openActivities.Add(item.Id, item);
            }

            if (item.ParentId != null)
            {
                ActivityTreeItem parent = null;
                if (!_openActivities.TryGetValue(item.ParentId, out parent))
                {
                    parent = new ActivityTreeItem(item.ParentId, null, "Unknown", item.StartTime);
                    _openActivities.Add(item.ParentId, parent);
                }

                parent.Children.Add(item);
            }
        }

        private void OnStopActivity(ActivityTreeItem item, TimeSpan duration)
        {
            item.Duration = duration;
            item.Stopped = true;
        }
    }

    public class ActivityTreeItem
    {
        public string Id { get; private set; }
        public string ParentId { get; set; }
        public string Description { get; set; }
        public List<ActivityTreeItem> Children { get; private set; }

        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }

        public bool Stopped { get; set; }

        public ActivityTreeItem(string id, string parentId, string description, DateTime startTime)
        {
            Id = id;
            ParentId = parentId;
            Description = description;
            StartTime = startTime;
            Children = new List<ActivityTreeItem>();
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            ToStringHelper(sb, 0);
            return sb.ToString();
        }

        private void ToStringHelper(StringBuilder sb, int indent)
        {
            sb.Append(' ', indent * 4);
            sb.AppendLine($"{StartTime} {Duration} {Description}");
            foreach(var child in Children)
            {
                child.ToStringHelper(sb, indent + 1);
            }
        }
    }
}
