using System.Threading.Tasks;

namespace Meds.Watchdog.Tasks
{
    public sealed class FullStartTask : ITask
    {
        public readonly ShutdownTask Shutdown;
        public readonly UpdateTask Update;
        public readonly StartTask Start;

        public FullStartTask(Program pgm)
        {
            Shutdown = new ShutdownTask(pgm);
            Update = new UpdateTask(pgm);
            Start = new StartTask(pgm);
        }

        public async Task Execute()
        {
            await Shutdown.Execute();
            await Update.Execute();
            await Start.Execute();
        }
    }
}