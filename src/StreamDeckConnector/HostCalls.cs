using MacroDeck.Plugin.Hosting.Transport;

namespace StreamDeckConnector;

/// <summary>
/// Macro Deck allows a plugin a burst of calls and then a steady rate. Start-up and the first paint of
/// a large deck exceed the burst, so a refused call is repeated instead of being lost.
/// </summary>
public static class HostCalls
{
	private const int MaxAttempts = 25;
	private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(200);

	public static bool IsRetryable(Exception exception) => exception is HostInvocationException { Retryable: true };

	public static Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken = default) =>
		RetryAsync(call, IsRetryable, _retryDelay, MaxAttempts, cancellationToken);

	public static async Task<T> RetryAsync<T>(Func<Task<T>> call, Func<Exception, bool> isRetryable, TimeSpan delay, int maxAttempts = MaxAttempts, CancellationToken cancellationToken = default)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				return await call().ConfigureAwait(false);
			}
			catch (Exception ex) when (attempt < maxAttempts && isRetryable(ex))
			{
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			}
		}
	}
}
