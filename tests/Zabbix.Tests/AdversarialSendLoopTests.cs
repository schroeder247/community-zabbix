// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.Sockets;
using Community.Zabbix.Hosting;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the send loop's retry: a handler with a defect (it throws on every batch, not an exchange
/// failure) must not hold the loop on one batch for ever, and a destination that is down must not be given up on.
/// </summary>
public sealed class AdversarialSendLoopTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_handler_that_always_throws_a_defect_rejects_the_batch_after_a_few_attempts_and_the_loop_goes_on()
    {
        var logs = new FakeLogCollector();
        var attempts = new Dictionary<int, int>();
        var delivered = new List<int>();
        await using var store = new ZabbixMemoryStore<int>();
        for (int i = 0; i < 4; i++) store.TryWrite(i);
        store.Complete();

        long taken = await ZabbixSendLoop.RunAsync<int>(store, (batch, _) =>
            {
                attempts[batch[0]] = attempts.GetValueOrDefault(batch[0]) + 1;
                if (batch[0] == 0) throw new NotSupportedException("the handler cannot serialise this value");
                delivered.AddRange(batch);
                return ValueTask.FromResult(ZabbixDelivery.Delivered);
            }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 2, RetryDelay = TimeSpan.FromMilliseconds(1) }, Time = TimeProvider.System, Logger = new FakeLogger(logs) }, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal(4, taken);
        Assert.Equal(ZabbixSendLoop.MaxDefectAttempts, attempts[0]);
        Assert.Equal([2, 3], delivered);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3103 && record.Level == LogLevel.Error && record.Exception is NotSupportedException);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3105);
    }

    [Fact]
    public async Task A_destination_that_is_down_for_longer_than_the_defect_budget_is_still_retried_until_it_answers()
    {
        int attempts = 0;
        var delivered = new List<int>();
        await using var store = new ZabbixMemoryStore<int>();
        store.TryWrite(7);
        store.Complete();

        long taken = await ZabbixSendLoop.RunAsync<int>(store, (batch, _) =>
            {
                if (Interlocked.Increment(ref attempts) <= ZabbixSendLoop.MaxDefectAttempts + 3) throw new SocketException((int)SocketError.ConnectionRefused);
                delivered.AddRange(batch);
                return ValueTask.FromResult(ZabbixDelivery.Delivered);
            }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { RetryDelay = TimeSpan.FromMilliseconds(1), MaxRetryDelay = TimeSpan.FromMilliseconds(2) }, Time = TimeProvider.System }, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal(1, taken);
        Assert.Equal([7], delivered);
        Assert.Equal(ZabbixSendLoop.MaxDefectAttempts + 4, attempts);
    }
}
