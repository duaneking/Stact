// Copyright 2010 Chris Patterson
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace Stact.Fibers
{
	using System;
	using System.ComponentModel;
	using System.Diagnostics;
	using System.Threading;
	using Magnum.Extensions;
	using Internal;
	using Magnum;
	using Magnum.Logging;

	[DebuggerDisplay("{GetType().Name} ( Count: {Count}, Next: {NextActionTime} )")]
	public class TimerScheduler :
		Scheduler
	{
		private static readonly ILogger _log = Logger.GetLogger<TimerScheduler>();

		private readonly ScheduledOperationList _operations = new ScheduledOperationList();
		private readonly Fiber _fiber;
		private readonly object _lock = new object();
		private readonly TimeSpan _noPeriod = -1.Milliseconds();
		private bool _stopped;
		private Timer _timer;

		public TimerScheduler(Fiber fiber)
		{
			_fiber = fiber;
		}

		private static DateTime Now
		{
			get { return SystemUtil.UtcNow; }
		}

		[EditorBrowsable(EditorBrowsableState.Never)]
		protected int Count
		{
			get { return _operations.Count; }
		}

		[EditorBrowsable(EditorBrowsableState.Never)]
		protected string NextActionTime
		{
			get
			{
				DateTime scheduledAt;
				if (_operations.GetNextScheduledTime(Now, out scheduledAt))
					return scheduledAt.ToString();

				return "None";
			}
		}

		public ScheduledOperation Schedule(int interval, Fiber fiber, Action action)
		{
			return Schedule(interval.Milliseconds(), fiber, action);
		}

		public ScheduledOperation Schedule(TimeSpan interval, Fiber fiber, Action action)
		{
			var scheduled = new ScheduledOperationExecuterImpl(GetScheduledTime(interval), fiber, action);
			Schedule(scheduled);

			return scheduled;
		}

		public ScheduledOperation Schedule(int interval, int periodicInterval, Fiber fiber, Action action)
		{
			return Schedule(interval.Milliseconds(), periodicInterval.Milliseconds(), fiber, action);
		}

		public ScheduledOperation Schedule(TimeSpan interval, TimeSpan periodicInterval, Fiber fiber, Action action)
		{
			ScheduledOperationExecuterImpl scheduled = null;
			scheduled = new ScheduledOperationExecuterImpl(GetScheduledTime(interval), fiber, () =>
				{
					try
					{
						action();
					}
					catch (Exception ex)
					{
						_log.Error(ex);
					}
					finally
					{
						scheduled.ScheduledAt = GetScheduledTime(periodicInterval);
						Schedule(scheduled);
					}
				});
			Schedule(scheduled);

			return scheduled;
		}

		public void Stop()
		{
			_stopped = true;

			lock (_lock)
			{
				if (_timer != null)
				{
					_timer.Dispose();
				}

				_fiber.Shutdown(60.Seconds());
			}
		}

		private void Schedule(ScheduledOperationExecuter action)
		{
			_fiber.Add(() =>
				{
					_operations.Add(action);

					ExecuteExpiredActions();
				});
		}

		private void ScheduleTimer()
		{
			DateTime now = Now;

			DateTime scheduledAt;
			if (_operations.GetNextScheduledTime(now, out scheduledAt))
			{
				lock (_lock)
				{
					TimeSpan dueTime = scheduledAt - now;

					if (_timer != null)
					{
						_timer.Change(dueTime, _noPeriod);
					}
					else
					{
						_timer = new Timer(x => _fiber.Add(ExecuteExpiredActions), this, dueTime, _noPeriod);
					}
				}
			}
		}

		private void ExecuteExpiredActions()
		{
			if (_stopped)
				return;

			ScheduledOperationExecuter[] expiredActions;
			while ((expiredActions = _operations.GetExpiredActions(Now)).Length > 0)
			{
				expiredActions.Each(action =>
					{
						try
						{
							action.Execute();
						}
						catch (Exception ex)
						{
							_log.Error(ex);
						}
					});
			}

			ScheduleTimer();
		}

		private static DateTime GetScheduledTime(TimeSpan interval)
		{
			return Now + interval;
		}
	}
}