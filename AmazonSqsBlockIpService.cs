using Amazon;
using Amazon.SQS;
using Bit.Core.Settings;
using System;
using System.Threading.Tasks;

namespace Bit.Core.Services;

public class AmazonSqsBlockIpService : IBlockIpService, IDisposable
{
    private readonly IAmazonSQS _client;
    private Lazy<Task<(string blockIpQueueUrl, string unblockIpQueueUrl)>> _queueUrls;
    private BlockedIpInfo _lastBlockedIp;

    public AmazonSqsBlockIpService(GlobalSettings globalSettings)
        : this(globalSettings, new AmazonSQSClient(
            globalSettings.Amazon.AccessKeyId,
            globalSettings.Amazon.AccessKeySecret,
            RegionEndpoint.GetBySystemName(globalSettings.Amazon.Region)))
    {
    }

    public AmazonSqsBlockIpService(GlobalSettings globalSettings, IAmazonSQS amazonSqs)
    {
        ValidateSettings(globalSettings.Amazon);
        _client = amazonSqs ?? throw new ArgumentNullException(nameof(amazonSqs));
        _queueUrls = new Lazy<Task<(string blockIpQueueUrl, string unblockIpQueueUrl)>>(InitAsync);
    }

    private static void ValidateSettings(AmazonSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings?.AccessKeyId))
            throw new ArgumentNullException(nameof(settings.AccessKeyId));
        if (string.IsNullOrWhiteSpace(settings?.AccessKeySecret))
            throw new ArgumentNullException(nameof(settings.AccessKeySecret));
        if (string.IsNullOrWhiteSpace(settings?.Region))
            throw new ArgumentNullException(nameof(settings.Region));
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    public async Task BlockIpAsync(string ipAddress, bool permanentBlock)
    {
        var now = DateTime.UtcNow;
        if (_lastBlockedIp?.Matches(ipAddress, permanentBlock, now) ?? false)
        {
            return; // Already blocked this IP recently.
        }

        _lastBlockedIp = new BlockedIpInfo(ipAddress, permanentBlock, now);

        var (blockIpQueueUrl, unblockIpQueueUrl) = await _queueUrls.Value;
        await _client.SendMessageAsync(blockIpQueueUrl, ipAddress);
        if (!permanentBlock)
        {
            await _client.SendMessageAsync(unblockIpQueueUrl, ipAddress);
        }
    }

    private async Task<(string blockIpQueueUrl, string unblockIpQueueUrl)> InitAsync()
    {
        var blockIpQueue = await _client.GetQueueUrlAsync("block-ip");
        var unblockIpQueue = await _client.GetQueueUrlAsync("unblock-ip");
        return (blockIpQueue.QueueUrl, unblockIpQueue.QueueUrl);
    }

    private class BlockedIpInfo
    {
        public string IpAddress { get; }
        public bool PermanentBlock { get; }
        public DateTime BlockTime { get; }

        public BlockedIpInfo(string ipAddress, bool permanentBlock, DateTime blockTime)
        {
            IpAddress = ipAddress;
            PermanentBlock = permanentBlock;
            BlockTime = blockTime;
        }

        public bool Matches(string ipAddress, bool permanentBlock, DateTime now)
        {
            return IpAddress == ipAddress && PermanentBlock == permanentBlock &&
                   (now - BlockTime) < TimeSpan.FromMinutes(1);
        }
    }
}
