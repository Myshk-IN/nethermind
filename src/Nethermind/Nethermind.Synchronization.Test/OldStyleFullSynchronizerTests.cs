// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class OldStyleFullSynchronizerTests
    {
        private readonly TimeSpan _standardTimeoutUnit = TimeSpan.FromMilliseconds(4000);

        [SetUp]
        public async Task Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            IDbProvider dbProvider = await TestMemDbProvider.InitAsync();
            _stateDb = dbProvider.StateDb;
            _codeDb = dbProvider.CodeDb;
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _ = new SyncConfig() { FastSync = false };

            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeStatsManager stats = new(timerFactory, LimboLogs.Instance);
            SyncConfig syncConfig = new()
            {
                MultiSyncModeSelectorLoopTimerMs = 1,
                SyncDispatcherEmptyRequestDelayMs = 1,
                SyncDispatcherAllocateTimeoutMs = 1
            };

            NodeStorage nodeStorage = new NodeStorage(_stateDb);
            TrieStore trieStore = new(nodeStorage, LimboLogs.Instance);
            TotalDifficultyBetterPeerStrategy bestPeerStrategy = new(LimboLogs.Instance);
            Pivot pivot = new(syncConfig);

            IStateReader stateReader = new StateReader(trieStore, _codeDb, LimboLogs.Instance);

            ContainerBuilder builder = new ContainerBuilder()
                .AddModule(new SynchronizerModule(syncConfig))
                .AddModule(new DbModule())
                .AddSingleton(dbProvider)
                .AddSingleton(nodeStorage)
                .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
                .AddSingleton(_blockTree)
                .AddSingleton(_receiptStorage)
                .AddSingleton<INodeStatsManager>(stats)
                .AddSingleton<ISyncConfig>(syncConfig)
                .AddSingleton<IBlockValidator>(Always.Valid)
                .AddSingleton<ISealValidator>(Always.Valid)
                .AddSingleton<IPivot>(pivot)
                .AddSingleton(Substitute.For<IProcessExitSource>())
                .AddSingleton<IBetterPeerStrategy>(bestPeerStrategy)
                .AddSingleton(new ChainSpec())
                .AddSingleton(stateReader)
                .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)
                .AddSingleton<IGossipPolicy>(Policy.FullGossip)
                .AddSingleton<ILogManager>(LimboLogs.Instance);

            IContainer container = builder.Build();

            _container = container;
        }

        [TearDown]
        public async Task TearDown()
        {
            await _container.DisposeAsync();
        }

        private IDb _stateDb = null!;
        private IDb _codeDb = null!;
        private IBlockTree _blockTree = null!;
        private IBlockTree _remoteBlockTree = null!;
        private IReceiptStorage _receiptStorage = null!;
        private Block _genesisBlock = null!;
        private ISyncPeerPool SyncPeerPool => _container.Resolve<ISyncPeerPool>();
        private ISyncServer SyncServer => _container.Resolve<ISyncServer>();
        private ISynchronizer Synchronizer => _container.Resolve<ISynchronizer>()!;
        private IContainer _container;

        [Test, Ignore("travis")]
        public void Retrieves_missing_blocks_in_batches()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max * 2).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSize.Max * 2 - 1));
        }

        [Test]
        public void Syncs_with_empty_peer()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(0));
        }

        [Test]
        public void Syncs_when_knows_more_blocks()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max * 2).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            _remoteBlockTree.Head?.Number.Should().NotBe(0);
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, _) => { resetEvent.Set(); };
            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSize.Max * 2 - 1));
        }

        [Test]
        [Ignore("TODO: review this test - failing only with other tests")]
        public void Can_resync_if_missed_a_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            SemaphoreSlim semaphore = new(0);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) semaphore.Release(1);
            };
            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            BlockTreeBuilder.ExtendTree(_remoteBlockTree, SyncBatchSize.Max * 2);
            SyncServer.AddNewBlock(_remoteBlockTree.RetrieveHeadBlock()!, peer);

            semaphore.Wait(_standardTimeoutUnit);
            semaphore.Wait(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSize.Max * 2 - 1));
        }

        [Test, Ignore("travis")]
        public void Can_add_new_block()
        {
            _remoteBlockTree = Build.A
                .BlockTree(_genesisBlock)
                .OfChainLength(SyncBatchSize.Max).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            Block block = Build.A.Block
                .WithParent(_remoteBlockTree.Head!)
                .WithTotalDifficulty((_remoteBlockTree.Head!.TotalDifficulty ?? 0) + 1)
                .TestObject;
            SyncServer.AddNewBlock(block, peer);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSize.Max - 1));
        }

        [Test]
        public void Can_sync_on_split_of_length_1()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            miner1Tree.BestSuggestedHeader.Should().BeEquivalentTo(_blockTree.BestSuggestedHeader, "client agrees with miner before split");

            Block splitBlock = Build.A.Block
                .WithParent(miner1Tree.FindParent(miner1Tree.Head!, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!)
                .WithDifficulty(miner1Tree.Head!.Difficulty - 1)
                .TestObject;
            Block splitBlockChild = Build.A.Block.WithParent(splitBlock).TestObject;

            miner1Tree.SuggestBlock(splitBlock);
            miner1Tree.UpdateMainChain(splitBlock);
            miner1Tree.SuggestBlock(splitBlockChild);
            miner1Tree.UpdateMainChain(splitBlockChild);

            splitBlockChild.Header.Should().BeEquivalentTo(miner1Tree.BestSuggestedHeader, "split as expected");

            resetEvent.Reset();

            SyncServer.AddNewBlock(splitBlockChild, miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(miner1Tree.BestSuggestedHeader!.Hash), "client agrees with miner after split");
        }

        [Test]
        public void Can_sync_on_split_of_length_6()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(miner1Tree.BestSuggestedHeader!.Hash), "client agrees with miner before split");

            miner1Tree.AddBranch(7, 0, 1);

            Assert.That(_blockTree.BestSuggestedHeader.Hash, Is.Not.EqualTo(miner1Tree.BestSuggestedHeader.Hash), "client does not agree with miner after split");

            resetEvent.Reset();

            SyncServer.AddNewBlock(miner1Tree.RetrieveHeadBlock()!, miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader.Hash, Is.EqualTo(miner1Tree.BestSuggestedHeader.Hash), "client agrees with miner after split");
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(minerTree);

            AutoResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(minerTree.BestSuggestedHeader!.Hash), "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head!).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.That((await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()))?.Number, Is.EqualTo(newBlock.Number), "number as expected");

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default);
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed_with_split()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(minerTree);

            AutoResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(minerTree.BestSuggestedHeader!.Hash), "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head!).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.That((await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()))?.Number, Is.EqualTo(newBlock.Number), "number as expected");

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default);
        }

        [Test]
        public void Can_retrieve_node_values()
        {
            _stateDb.Set(TestItem.KeccakA, TestItem.RandomDataA);
            IOwnedReadOnlyList<byte[]?> data = SyncServer.GetNodeData(new[] { TestItem.KeccakA, TestItem.KeccakB }, CancellationToken.None);

            Assert.That(data, Is.Not.Null);
            Assert.That(data.Count, Is.EqualTo(2), "data.Length");
            Assert.That(data[0], Is.EqualTo(TestItem.RandomDataA), "data[0]");
            Assert.That(data[1], Is.EqualTo(null), "data[1]");
        }

        [Test]
        public void Can_retrieve_empty_receipts()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            Block? block0 = _blockTree.FindBlock(0, BlockTreeLookupOptions.None);
            Block? block1 = _blockTree.FindBlock(1, BlockTreeLookupOptions.None);

            SyncServer.GetReceipts(block0!.Hash!).Should().HaveCount(0);
            SyncServer.GetReceipts(block1!.Hash!).Should().HaveCount(0);
            SyncServer.GetReceipts(TestItem.KeccakA).Should().HaveCount(0);
        }
    }
}
