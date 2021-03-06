﻿using MagicalCryptoWallet.Backend.Models;
using MagicalCryptoWallet.Helpers;
using MagicalCryptoWallet.Logging;
using MagicalCryptoWallet.Models;
using NBitcoin;
using NBitcoin.RPC;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MagicalCryptoWallet.Services
{
	public class IndexBuilderService
	{
		public RPCClient RpcClient { get; }
		public string IndexFilePath { get; }
		public string Bech32UtxoSetFilePath { get; }

		private List<FilterModel> Index { get; }
		private AsyncLock IndexLock { get; }

		private Dictionary<OutPoint, Script> Bech32UtxoSet { get; }
		private List<ActionHistoryHelper> Bech32UtxoSetHistory { get; }

		private class ActionHistoryHelper
		{
			public enum Operation
			{
				Add,
				Remove
			}

			private List<ActionItem> ActionHistory { get; }

			public ActionHistoryHelper()
			{
				ActionHistory = new List<ActionItem>();
			}

			public class ActionItem
			{
				public Operation Action { get; }
				public OutPoint OutPoint { get; }
				public Script Script { get; }

				public ActionItem(Operation action, OutPoint outPoint, Script script)
				{
					Action = action;
					OutPoint = outPoint;
					Script = script;
				}
			}

			public void ClearActionHistory()
			{
				ActionHistory.Clear();
			}

			public void StoreAction(ActionItem actionItem)
			{
				ActionHistory.Add(actionItem);
			}
			public void StoreAction(Operation action, OutPoint outpoint, Script script)
			{
				StoreAction(new ActionItem(action, outpoint, script));
			}

			public void Rollback(Dictionary<OutPoint, Script> toRollBack)
			{
				for (var i = ActionHistory.Count - 1; i >= 0; i--)
				{
					ActionItem act = ActionHistory[i];
					switch (act.Action)
					{
						case Operation.Add:
							toRollBack.Remove(act.OutPoint);
							break;
						case Operation.Remove:
							toRollBack.Add(act.OutPoint, act.Script);
							break;
						default:
							throw new ArgumentOutOfRangeException();
					}
				}
				ActionHistory.Clear();
			}
		}

		public static Height GetStartingHeight(Network network) // First possible bech32 transaction ever.
		{
			if (network == Network.Main)
			{
				return new Height(481824);
			}
			else if (network == Network.TestNet)
			{
				return new Height(828575);
			}
			else if (network == Network.RegTest)
			{
				return new Height(0);
			}
			else
			{
				throw new NotSupportedException($"{network} is not supported.");
			}
		}
		public Height StartingHeight => GetStartingHeight(RpcClient.Network);
		public static FilterModel GetStartingFilter(Network network) => IndexDownloader.GetStartingFilter(network);
		public FilterModel StartingFilter => GetStartingFilter(RpcClient.Network);

		/// <summary>
		/// 0: Not started, 1: Running, 2: Stopping, 3: Stopped
		/// </summary>
		private long _running;
		public bool IsRunning => Interlocked.Read(ref _running) == 1;
		public bool IsStopping => Interlocked.Read(ref _running) == 2;

		public IndexBuilderService(RPCClient rpc, string indexFilePath, string bech32UtxoSetFilePath)
		{
			RpcClient = Guard.NotNull(nameof(rpc), rpc);
			IndexFilePath = Guard.NotNullOrEmptyOrWhitespace(nameof(indexFilePath), indexFilePath);
			Bech32UtxoSetFilePath = Guard.NotNullOrEmptyOrWhitespace(nameof(bech32UtxoSetFilePath), bech32UtxoSetFilePath);

			Bech32UtxoSet = new Dictionary<OutPoint, Script>();
			Bech32UtxoSetHistory = new List<ActionHistoryHelper>(capacity: 100);
			Index = new List<FilterModel>();
			IndexLock = new AsyncLock();

			_running = 0;

			var indexDir = Path.GetDirectoryName(IndexFilePath);
			Directory.CreateDirectory(indexDir);
			if (File.Exists(IndexFilePath))
			{
				if (RpcClient.Network == Network.RegTest)
				{
					File.Delete(IndexFilePath); // RegTest is not a global ledger, better to delete it.
				}
				else
				{
					int height = StartingHeight.Value;
					foreach (var line in File.ReadAllLines(IndexFilePath))
					{
						var filter = FilterModel.FromLine(line, new Height(height));
						height++;
						Index.Add(filter);
					}
				}
			}

			var utxoSetDir = Path.GetDirectoryName(bech32UtxoSetFilePath);
			Directory.CreateDirectory(utxoSetDir);
			if (File.Exists(bech32UtxoSetFilePath))
			{
				if (RpcClient.Network == Network.RegTest)
				{
					File.Delete(bech32UtxoSetFilePath); // RegTest is not a global ledger, better to delete it.
				}
				else
				{
					foreach (var line in File.ReadAllLines(Bech32UtxoSetFilePath))
					{
						var parts = line.Split(':');

						var txHash = new uint256(parts[0]);
						var nIn = int.Parse(parts[1]);
						var script = new Script(ByteHelpers.FromHex(parts[2]), true);
						Bech32UtxoSet.Add(new OutPoint(txHash, nIn), script);
					}
				}
			}
		}

		public void Synchronize()
		{
			Interlocked.Exchange(ref _running, 1);

			Task.Run(async () =>
			{
				try
				{
					var blockCount = await RpcClient.GetBlockCountAsync();
					var isIIB = true; // Initial Index Building phase

					while (IsRunning)
					{
						try
						{
							// If stop was requested return.
							if (IsRunning == false) return;

							int height = StartingHeight.Value;
							uint256 prevHash = null;
							using (await IndexLock.LockAsync())
							{
								if (Index.Count != 0)
								{
									height = Index.Last().BlockHeight.Value + 1;
									prevHash = Index.Last().BlockHash;
								}
							}

							if (blockCount - height <= 100)
							{
								isIIB = false;
							}

							Block block = null;
							try
							{
								block = await RpcClient.GetBlockAsync(height);
							}
							catch (RPCException) // if the block didn't come yet
							{
								await Task.Delay(1000);
								continue;
							}

							if (prevHash != null)
							{
								// In case of reorg:
								if (prevHash != block.Header.HashPrevBlock && !isIIB) // There is no reorg in IIB
								{
									Logger.LogInfo<IndexBuilderService>($"REORG Invalid Block: {prevHash}");
									// 1. Rollback index
									using (await IndexLock.LockAsync())
									{
										Index.RemoveLast();
									}

									// 2. Serialize Index. (Remove last line.)
									var lines = File.ReadAllLines(IndexFilePath);
									File.WriteAllLines(IndexFilePath, lines.Take(lines.Length - 1).ToArray());

									// 3. Rollback Bech32UtxoSet
									if (Bech32UtxoSetHistory.Count != 0)
									{
										Bech32UtxoSetHistory.Last().Rollback(Bech32UtxoSet); // The Bech32UtxoSet MUST be recovered to its previous state.
										Bech32UtxoSetHistory.RemoveLast();

										// 4. Serialize Bech32UtxoSet.
										await File.WriteAllLinesAsync(Bech32UtxoSetFilePath, Bech32UtxoSet
											.Select(entry => entry.Key.Hash + ":" + entry.Key.N + ":" + ByteHelpers.ToHex(entry.Value.ToCompressedBytes())));
									}

									// 5. Skip the current block.
									continue;
								}
							}

							if (!isIIB)
							{
								if (Bech32UtxoSetHistory.Count >= 100)
								{
									Bech32UtxoSetHistory.RemoveFirst();
								}
								Bech32UtxoSetHistory.Add(new ActionHistoryHelper());
							}

							var scripts = new HashSet<Script>();

							foreach (var tx in block.Transactions)
							{
								for (int i = 0; i < tx.Outputs.Count; i++)
								{
									var output = tx.Outputs[i];
									if (!output.ScriptPubKey.IsPayToScriptHash && output.ScriptPubKey.IsWitness)
									{
										var outpoint = new OutPoint(tx.GetHash(), i);
										Bech32UtxoSet.Add(outpoint, output.ScriptPubKey);
										if (!isIIB)
										{
											Bech32UtxoSetHistory.Last().StoreAction(ActionHistoryHelper.Operation.Add, outpoint, output.ScriptPubKey);
										}
										scripts.Add(output.ScriptPubKey);
									}
								}

								foreach (var input in tx.Inputs)
								{
									var found = Bech32UtxoSet.SingleOrDefault(x => x.Key == input.PrevOut);
									if (found.Key != default)
									{
										Script val = Bech32UtxoSet[input.PrevOut];
										Bech32UtxoSet.Remove(input.PrevOut);
										if (!isIIB)
										{
											Bech32UtxoSetHistory.Last().StoreAction(ActionHistoryHelper.Operation.Remove, input.PrevOut, val);
										}
										scripts.Add(found.Value);
									}
								}
							}

							// https://github.com/bitcoin/bips/blob/master/bip-0158.mediawiki
							// The parameter k MUST be set to the first 16 bytes of the hash of the block for which the filter 
							// is constructed.This ensures the key is deterministic while still varying from block to block.
							var key = block.GetHash().ToBytes().Take(16).ToArray();

							GolombRiceFilter filter = null;
							if (scripts.Count != 0)
							{
								filter = GolombRiceFilter.Build(key, scripts.Select(x => x.ToCompressedBytes()));
							}

							var filterModel = new FilterModel
							{
								BlockHash = block.GetHash(),
								BlockHeight = new Height(height),
								Filter = filter
							};

							await File.AppendAllLinesAsync(IndexFilePath, new[] { filterModel.ToLine() });
							using (await IndexLock.LockAsync())
							{
								Index.Add(filterModel);
							}
							if (File.Exists(Bech32UtxoSetFilePath))
							{
								File.Delete(Bech32UtxoSetFilePath);
							}
							await File.WriteAllLinesAsync(Bech32UtxoSetFilePath, Bech32UtxoSet
								.Select(entry => entry.Key.Hash + ":" + entry.Key.N + ":" + ByteHelpers.ToHex(entry.Value.ToCompressedBytes())));

							Logger.LogInfo<IndexBuilderService>($"Created filter for block: {height}.");
						}
						catch (Exception ex)
						{
							Logger.LogDebug<IndexBuilderService>(ex);
						}
					}
				}
				finally
				{
					if (IsStopping)
					{
						Interlocked.Exchange(ref _running, 3);
					}
				}
			});
		}

		public IEnumerable<string> GetFilterLinesExcluding(uint256 bestKnownBlockHash, out bool found)
		{
			using (IndexLock.Lock())
			{
				found = false;
				var filters = new List<string>();
				foreach (var filter in Index)
				{
					if (found)
					{
						filters.Add(filter.ToLine());
					}
					else
					{
						if (filter.BlockHash == bestKnownBlockHash)
						{
							found = true;
						}
					}
				}

				return filters;
			}
		}

		public async Task StopAsync()
		{
			if (IsRunning)
			{
				Interlocked.Exchange(ref _running, 2);
			}
			while (IsStopping)
			{
				await Task.Delay(50);
			}
		}
	}
}
