﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeStream.Stratis.Bitcoin.Configuration;
using NBitcoin;
using NBitcoin.Protocol;
using Stratis.Bitcoin;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Features.Api;
using Stratis.Bitcoin.Features.BlockStore;
using Stratis.Bitcoin.Features.Consensus;
using Stratis.Bitcoin.Features.MemoryPool;
using Stratis.Bitcoin.Features.Miner;
using Stratis.Bitcoin.Features.RPC;
using Stratis.Bitcoin.Features.Wallet;
using Stratis.Bitcoin.Features.Wallet.Interfaces;
using Stratis.Bitcoin.IntegrationTests.Common;
using Stratis.Bitcoin.IntegrationTests.Common.EnvironmentMockUpHelpers;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.Utilities;
using static Stratis.Bitcoin.BlockPulling.BlockPuller;

namespace DeStream.DeStreamD.ForTest
{
    public class Program
    {
        public static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }
        private class TransactionNode
        {
            public uint256 Hash = null;
            public Transaction Transaction = null;
            public List<TransactionNode> DependsOn = new List<TransactionNode>();

            public TransactionNode(Transaction tx)
            {
                this.Transaction = tx;
                this.Hash = tx.GetHash();
            }
        }

        private static List<Transaction> Reorder(List<Transaction> transactions)
        {
            if (transactions.Count == 0)
                return transactions;

            var result = new List<Transaction>();
            Dictionary<uint256, TransactionNode> dictionary = transactions.ToDictionary(t => t.GetHash(), t => new TransactionNode(t));
            foreach (TransactionNode transaction in dictionary.Select(d => d.Value))
            {
                foreach (TxIn input in transaction.Transaction.Inputs)
                {
                    TransactionNode node = dictionary.TryGet(input.PrevOut.Hash);
                    if (node != null)
                    {
                        transaction.DependsOn.Add(node);
                    }
                }
            }

            while (dictionary.Count != 0)
            {
                foreach (TransactionNode node in dictionary.Select(d => d.Value).ToList())
                {
                    foreach (TransactionNode parent in node.DependsOn.ToList())
                    {
                        if (!dictionary.ContainsKey(parent.Hash))
                            node.DependsOn.Remove(parent);
                    }

                    if (node.DependsOn.Count == 0)
                    {
                        result.Add(node.Transaction);
                        dictionary.Remove(node.Hash);
                    }
                }
            }

            return result;
        }

        public static Block[] GenerateStratis(IFullNode node, BitcoinSecret dest, int blockCount, List<Transaction> passedTransactions = null, bool broadcast = true)
        {
            FullNode fullNode = (FullNode)node;
            //BitcoinSecret dest = this.MinerSecret;
            var blocks = new List<Block>();
            //DateTimeOffset now = this.MockTime == null ? DateTimeOffset.UtcNow : this.MockTime.Value;
            
            for (int i = 0; i < blockCount; i++)
            {
                uint nonce = 0;
                var block = new Block();
                block.Header.HashPrevBlock = fullNode.Chain.Tip.HashBlock;
                block.Header.Bits = block.Header.GetWorkRequired(fullNode.Network, fullNode.Chain.Tip);
                block.Header.UpdateTime(DateTimeOffset.UtcNow, fullNode.Network, fullNode.Chain.Tip);
                var coinbase = new Transaction();
                coinbase.AddInput(TxIn.CreateCoinbase(fullNode.Chain.Height + 1));
                coinbase.AddOutput(new TxOut(fullNode.Network.GetReward(fullNode.Chain.Height + 1), dest.GetAddress()));
                block.AddTransaction(coinbase);
                if (passedTransactions?.Any() ?? false)
                {
                    passedTransactions = Reorder(passedTransactions);
                    block.Transactions.AddRange(passedTransactions);
                }
                block.UpdateMerkleRoot();
                while (!block.CheckProofOfWork())
                    block.Header.Nonce = ++nonce;
                blocks.Add(block);
                if (broadcast)
                {
                    uint256 blockHash = block.GetHash();
                    var newChain = new ChainedHeader(block.Header, blockHash, fullNode.Chain.Tip);
                    ChainedHeader oldTip = fullNode.Chain.SetTip(newChain);
                    fullNode.ConsensusLoop().Puller.InjectBlock(blockHash, new DownloadedBlock { Length = block.GetSerializedSize(), Block = block }, CancellationToken.None);
                }
            }

            return blocks.ToArray();
        }
        public static async Task MainAsync(string[] args)
        {
            try
            {
                Network network = null;
                if (args.Contains("-testnet"))
                    network = Network.DeStreamTest;
                else
                    network = Network.DeStreamMain;

                DeStreamNodeSettings nodeSettings = new DeStreamNodeSettings(network, ProtocolVersion.ALT_PROTOCOL_VERSION, args: args, loadConfiguration: false);

                Console.WriteLine($"current network: {network.Name}");

                // NOTES: running BTC and STRAT side by side is not possible yet as the flags for serialization are static
                FullNode node = (FullNode)new FullNodeBuilder()
                    .UseNodeSettings(nodeSettings)
                    .UseBlockStore()
                    .UsePosConsensus()
                    .UseMempool()
                    .UseWallet()
                    .AddPowPosMining()
                    .UseApi()
                    .AddRPC()
                    .Build();
                if (node != null)
                    await node.RunAsync();

                (node.NodeService<IInitialBlockDownloadState>() as InitialBlockDownloadStateMock).SetIsInitialBlockDownload(false, DateTime.UtcNow.AddMinutes(5));

                Mnemonic _mnemonic1 = node.NodeService<IWalletManager>().CreateWallet("123456", "mywallet");
                //Mnemonic _mnemonic2 = node.NodeService<IWalletManager>().CreateWallet("123456", "mywallet");
                HdAddress _addr = node.NodeService<IWalletManager>().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Wallet _wallet = node.NodeService<IWalletManager>().GetWalletByName("mywallet");
                Key _key = _wallet.GetExtendedPrivateKeyForAddress("123456", _addr).PrivateKey;

                BitcoinSecret bitcoinSecret = new BitcoinSecret(_key, node.Network);


                int _maturity = (int)node.Network.Consensus.CoinbaseMaturity;

                GenerateStratis(node, bitcoinSecret, 10);

                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced((FullNode)node));
                //// wait for block repo for block sync to work

                //TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));

                //// the mining should add coins to the wallet
                //long total = stratisSender.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);

                //var walletManager = ((FullNode)node).NodeService<IWalletManager>() as WalletManager;
                ////HdAddress addr = ((FullNode)node).WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                //walletManager.CreateWallet("123456", "mywallet");
                //HdAddress sendto = walletManager.GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                //var walletTransactionHandler = ((FullNode)node).NodeService<IWalletTransactionHandler>() as WalletTransactionHandler;

                //var transactionBuildContext = CreateContext(
                //    new WalletAccountReference("mywallet", "account 0"), "123456", sendto.ScriptPubKey, Money.COIN * 100, FeeType.Medium, 101);

                //Transaction trx = walletTransactionHandler.BuildTransaction(transactionBuildContext);


                int qwe = 1;
                
                NodeBuilder builder = NodeBuilder.Create(node);
                CoreNode stratisSender = builder.CreateStratisPowNode();
                CoreNode stratisReceiver = builder.CreateStratisPowNode();
                builder.StartAll();
                stratisSender.NotInIBD();
                stratisReceiver.NotInIBD();

                // get a key from the wallet
                Mnemonic mnemonic1 = stratisSender.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                Mnemonic mnemonic2 = stratisReceiver.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                HdAddress addr = stratisSender.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Wallet wallet = stratisSender.FullNode.WalletManager().GetWalletByName("mywallet");
                Key key = wallet.GetExtendedPrivateKeyForAddress("123456", addr).PrivateKey;

                stratisSender.SetDummyMinerSecret(new BitcoinSecret(key, stratisSender.FullNode.Network));
                int maturity = (int)stratisSender.FullNode.Network.Consensus.CoinbaseMaturity;
                stratisSender.GenerateStratis(maturity + 5);
                // wait for block repo for block sync to work

                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));

                // the mining should add coins to the wallet
                long total = stratisSender.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);

                var walletManager = stratisSender.FullNode.NodeService<IWalletManager>() as WalletManager;

                //var walletManager1 = ((FullNode)node).NodeService<IWalletManager>() as WalletManager;

                //HdAddress addr = ((FullNode)node).WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                walletManager.CreateWallet("123456", "mywallet");
                HdAddress sendto = walletManager.GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                var walletTransactionHandler = ((FullNode)node).NodeService<IWalletTransactionHandler>() as WalletTransactionHandler;

                var transactionBuildContext = CreateContext(
                    new WalletAccountReference("mywallet", "account 0"), "123456", sendto.ScriptPubKey, Money.COIN * 100, FeeType.Medium, 101);

                Transaction trx = walletTransactionHandler.BuildTransaction(transactionBuildContext);

                if (node != null)
                    await node.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("There was a problem initializing the node. Details: '{0}'", ex.Message);
            }
        }

        public static TransactionBuildContext CreateContext(WalletAccountReference accountReference, string password,
            Script destinationScript, Money amount, FeeType feeType, int minConfirmations)
        {
            return new TransactionBuildContext(accountReference,
                new[] { new Recipient { Amount = amount, ScriptPubKey = destinationScript } }.ToList(), password)
            {
                MinConfirmations = minConfirmations,
                FeeType = feeType
            };
        }


    }
}