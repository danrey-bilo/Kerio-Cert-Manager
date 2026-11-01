namespace Modules.Mail.Sending;

/// <summary>Сигнал насосу: в очередь легло письмо, спать больше не надо.</summary>
internal sealed class MailWakeup
{
    private readonly SemaphoreSlim _сигнал = new(0, 1);

    /// <summary>В очередь легло письмо.</summary>
    public void Signal()
    {
        if (_сигнал.CurrentCount == 0)
        {
            try
            {
                _сигнал.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>Подождать письма или срока.</summary>
    public async Task<bool> WaitAsync(TimeSpan сколько, CancellationToken cancellationToken) =>
        await _сигнал.WaitAsync(сколько, cancellationToken);
}
