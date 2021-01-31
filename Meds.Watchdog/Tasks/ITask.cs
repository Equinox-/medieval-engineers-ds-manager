using System.Threading.Tasks;

namespace Meds.Watchdog.Tasks
{
    public interface ITask
    {
        Task Execute();
    }
}