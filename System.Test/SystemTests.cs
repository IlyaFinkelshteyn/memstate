namespace System.Test
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Memstate;

    using Microsoft.Extensions.Logging;

    using Xunit;
    using Xunit.Abstractions;

    public class SystemTests
    {
        private readonly ITestOutputHelper _log;
        private readonly string _randomStreamName;

        public SystemTests(ITestOutputHelper log)
        {
            _log = log;
            _randomStreamName = "memstate" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        [Theory]
        [ClassData(typeof(TestConfigurations))]
        public async Task CanWriteOne(MemstateSettings settings)
        {
            var logProvider = new TestOutputLoggingProvider(_log);
            logProvider.MinimumLogLevel = LogLevel.Trace;
            settings.LoggerFactory.AddProvider(logProvider);
            settings.StreamName = _randomStreamName;
            _log.WriteLine(settings.ToString());

            var provider = settings.CreateStorageProvider();
            provider.Initialize();
            var writer = provider.CreateJournalWriter(0);

            writer.Send(new AddStringCommand("hello"));
            await writer.DisposeAsync().ConfigureAwait(false);

            var reader = provider.CreateJournalReader();
            var records = reader.GetRecords().ToArray();
            await reader.DisposeAsync().ConfigureAwait(false);
            Assert.Single(records);
        }

        [Theory]
        [ClassData(typeof(TestConfigurations))]
        public async Task WriteAndReadCommands(MemstateSettings settings)
        {
            settings.LoggerFactory.AddProvider(new TestOutputLoggingProvider(_log));
            settings.StreamName = _randomStreamName;

            var provider = settings.CreateStorageProvider();
            provider.Initialize();

            var journalWriter = provider.CreateJournalWriter(0);

            for (var i = 0; i < 10000; i++)
            {
                journalWriter.Send(new AddStringCommand(i.ToString()));
            }

            await journalWriter.DisposeAsync().ConfigureAwait(false);
            var journalReader = provider.CreateJournalReader();
            var records = journalReader.GetRecords().ToArray();
            await journalReader.DisposeAsync().ConfigureAwait(false);
            Assert.Equal(10000, records.Length);
        }

        [Theory]
        [ClassData(typeof(TestConfigurations))]
        public async Task SubscriptionDeliversPreExistingCommands(MemstateSettings settings)
        {
            settings.LoggerFactory.AddProvider(new TestOutputLoggingProvider(_log));
            settings.StreamName = _randomStreamName;

            var provider = settings.CreateStorageProvider();
            const int NumRecords = 50;
            var journalWriter = provider.CreateJournalWriter(0);
            for (var i = 0; i < NumRecords; i++)
            {
                journalWriter.Send(new AddStringCommand(i.ToString()));
            }

            await journalWriter.DisposeAsync().ConfigureAwait(false);

            var records = new List<JournalRecord>();
            var subSource = provider.CreateJournalSubscriptionSource();

            if (!provider.SupportsCatchupSubscriptions())
            {
                Assert.Throws<NotSupportedException>(() => subSource.Subscribe(0, records.Add));
            }
            else
            {
                subSource.Subscribe(0, records.Add);
                await WaitForConditionOrThrow(() => records.Count == NumRecords).ConfigureAwait(false);
                Assert.Equal(Enumerable.Range(0, NumRecords), records.Select(r => (int)r.RecordNumber));
            }
        }

        [Theory]
        [ClassData(typeof(TestConfigurations))]
        public async Task SubscriptionDeliversFutureCommands(MemstateSettings settings)
        {
            const int NumRecords = 5;

            settings.LoggerFactory.AddProvider(new TestOutputLoggingProvider(_log));
            settings.StreamName = _randomStreamName;

            var provider = settings.CreateStorageProvider();
            var records = new List<JournalRecord>();
            var writer = provider.CreateJournalWriter(0);

            var subSource = provider.CreateJournalSubscriptionSource();
            var sub = subSource.Subscribe(0, records.Add);

            for (var i = 0; i < NumRecords; i++)
            {
                writer.Send(new AddStringCommand(i.ToString()));
            }

            await writer.DisposeAsync().ConfigureAwait(false);
            await WaitForConditionOrThrow(() => records.Count == 5).ConfigureAwait(false);
            sub.Dispose();

            Assert.Equal(NumRecords, records.Count);
        }

        [Theory]
        [ClassData(typeof(TestConfigurations))]
        public async Task Can_execute_void_commands(MemstateSettings settings)
        {
            settings.LoggerFactory.AddProvider(new TestOutputLoggingProvider(_log));
            settings.StreamName = _randomStreamName;

            var engine = await Engine.StartAsync<List<string>>(settings).ConfigureAwait(false);
            await engine.ExecuteAsync(new Reverse()).ConfigureAwait(false);
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        [Theory]
        [ClassData(typeof(TestConfigurations))]
        public async Task Smoke(MemstateSettings settings)
        {
            const int NumRecords = 100;

            settings.LoggerFactory.AddProvider(new TestOutputLoggingProvider(_log));
            settings.StreamName = _randomStreamName;

            var engine = await Engine.StartAsync<List<string>>(settings).ConfigureAwait(false);

            foreach (var number in Enumerable.Range(1, NumRecords))
            {
                var command = new AddStringCommand(number.ToString());
                var count = await engine.ExecuteAsync(command).ConfigureAwait(false);
                Assert.Equal(number, count);
            }

            await engine.DisposeAsync().ConfigureAwait(false);

            engine = await Engine.StartAsync<List<string>>(settings).ConfigureAwait(false);
            var strings = await engine.ExecuteAsync(new GetStringsQuery()).ConfigureAwait(false);
            Assert.Equal(NumRecords, strings.Count);
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        private async Task WaitForConditionOrThrow(Func<bool> condition, TimeSpan? checkInterval = null, int numberOfTries = 25)
        {
            checkInterval = checkInterval ?? TimeSpan.FromMilliseconds(50);
            while (!condition.Invoke())
            {
                await Task.Delay(checkInterval.Value).ConfigureAwait(false);
                if (numberOfTries-- == 0)
                {
                    throw new TimeoutException();
                }
            }
        }
    }
}