using System.Threading.Tasks;

namespace Meds.Watchdog.Tasks
{
    public sealed class FullStartTask : ITask
    {
        private readonly ShutdownTask _shutdown;
        private readonly UpdateTask _update;
        private readonly StartTask _start;

        public FullStartTask(Program pgm)
        {
            _shutdown = new ShutdownTask(pgm);
            _update = new UpdateTask(pgm);
            _start = new StartTask(pgm);
        }

        public async Task Execute()
        {
            await _shutdown.Execute();
            await _update.Execute();
            await _start.Execute();
        }
    }
}