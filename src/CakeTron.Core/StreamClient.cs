using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CakeTron.Core.Diagnostics;
using CakeTron.Core.Threading;

namespace CakeTron.Core
{
    public sealed class StreamClient : ManagedThread<StreamClientContext>
    {
        private readonly HttpClient _client;
        private readonly IInbox _inbox;
        private readonly EngineSettings _settings;
        private readonly ILog _log;
        private readonly GitterClient _gitter;

        public override string FriendlyName => "Stream API client";

        public StreamClient(EngineSettings settings, IInbox inbox, HttpClient client, GitterClient gitter, ILog log)
            : base(log)
        {
            _client = client;
            _inbox = inbox;
            _settings = settings;
            _log = log;
            _gitter = gitter;
        }

        protected override StreamClientContext CreateContext()
        {
            // Get all rooms.
            _log.Debug("Getting rooms...");
            var rooms = _gitter.GetRooms().Result.ToArray();
            if (_settings.Rooms != null && _settings.Rooms.Length > 0)
            {
                // Filter rooms.
                rooms = rooms
                    .Where(r => !r.OneToOne && _settings.Rooms.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }
            _log.Debug("Found {0} available rooms.", rooms.Length);

            // Who are we?
            _log.Debug("Getting current user...");
            var bot = _gitter.GetCurrentUser().Result;
            _log.Debug("Current user is {0}.", bot.Username);

            return new StreamClientContext(bot, rooms);
        }

        protected override void Execute(StreamClientContext context, CancellationTokenSource source)
        {
            // Create a task for each room.
            var tasks = new Task[context.Rooms.Length];
            for (var index = 0; index < context.Rooms.Length; index++)
            {
                tasks[index] = Task.Factory.StartNew(obj =>
                    new StreamListener(_log).Listen(obj as StreamListenerContext, message => _inbox.Enqueue(message)).Wait(),
                    new StreamListenerContext(_client, context.Rooms[index], context.Bot, source.Token),
                    source.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
            }

            try
            {
                // Wait for any task to exit or a request to cancel.
                var index = Task.WaitAny(tasks, source.Token);
                if (!source.Token.IsCancellationRequested && index > 0 && index < context.Rooms.Length)
                {
                    // Telling tasks to quit.
                    _log.Information("The listener for {0} died.", context.Rooms[index]);
                    _log.Information("Telling all listeners to cancel...");
                    source.Cancel(false);
                }
            }
            catch (OperationCanceledException)
            {
                _log.Information("Stream client was requested to stop.");
                if (!source.Token.IsCancellationRequested)
                {
                    _log.Information("Telling all listeners to cancel...");
                    source.Cancel(false);
                }
            }

            WaitForTasksToStop(tasks);
        }

        private void WaitForTasksToStop(Task[] tasks)
        {
            try
            {
                _log.Information("Waiting for listeners to stop...");
                Task.WaitAll(tasks);
                _log.Information("All listeners stopped.");
            }
            catch (AggregateException ex)
            {
                foreach (var inner in ex.InnerExceptions)
                {
                    var exception = inner as TaskCanceledException;
                    if (exception == null)
                    {
                        _log.Error("AggregateException: {Error}", inner.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Exception: {Error}", ex.Message);
            }
        }
    }
}