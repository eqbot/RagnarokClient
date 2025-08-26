using MareSynchronos.Services.Mediator;
using Microsoft.Extensions.Logging;
using MareSynchronos.WebAPI.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MareSynchronos.MareConfiguration;
using Ipfs.Http;
using Ipfs;
using FFXIVClientStructs.FFXIV.Component.SteamApi;
using Ipfs.CoreApi;

namespace MareSynchronos.WebAPI.Files
{
    public sealed class IpfsManager : DisposableMediatorSubscriberBase
    {
        private readonly IpfsClient _ipfsClient;
        private readonly MareConfigService _mareConfig;
        public IpfsManager(ILogger<IpfsManager> logger, MareConfigService mareConfig,
        MareMediator mediator, TokenProvider tokenProvider) : base(logger, mediator)
        {
            _mareConfig = mareConfig;
            _ipfsClient = new IpfsClient();
        }

        public async Task<Cid> UploadFile(string path)
        {
            var node = await _ipfsClient.FileSystem.AddFileAsync(path).ConfigureAwait(false);
            return node.Id;
        }

        public async Task<Cid> UploadFile(Stream data)
        {
            var node = await _ipfsClient.FileSystem.AddAsync(data).ConfigureAwait(false);
            return node.Id;
        }

        public async Task<Stream> DownloadFile(string path)
        {
            return await _ipfsClient.FileSystem.ReadFileAsync(path).ConfigureAwait(false);
        }

        public async Task<Cid> GetCid(string path)
        {
            var options = new AddFileOptions { OnlyHash = true };
            var node = await _ipfsClient.FileSystem.AddFileAsync(path, options).ConfigureAwait(false);
            return node.Id;
        }

        public async Task<long> GetSize(string cid)
        {
            var node = await _ipfsClient.FileSystem.ListAsync(cid).ConfigureAwait(false);
            //TODO: don't downcast this ulong to long for precision
            return (long)node.Size;
        }
    }
}
