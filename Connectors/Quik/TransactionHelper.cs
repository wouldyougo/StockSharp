#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Quik.QuikPublic
File: TransactionHelper.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Quik
{
	using System;
	using System.Collections.Generic;

	using Ecng.Common;

	using StockSharp.Algo;
	using StockSharp.BusinessEntities;
	using StockSharp.Messages;
	using StockSharp.Localization;

	static class TransactionHelper
	{
		public static Transaction CreateMoveTransaction(this OrderReplaceMessage message, IDictionary<string, RefPair<SecurityTypes, string>> securityClassInfo)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			if (message.OrderType == OrderTypes.ExtRepo || message.OrderType == OrderTypes.Repo || message.OrderType == OrderTypes.Rps)
			{
				throw new ArgumentOutOfRangeException(nameof(message), message.Type, LocalizedStrings.Str1847);
			}

			if (message.OldOrderId == null)
				throw new InvalidOperationException(LocalizedStrings.Str2252Params.Put(message.OldTransactionId));

			return
				new Transaction(TransactionTypes.ReRegister, message)
					.SetAction(TransactionActions.MoveOrders)
					.SetSecurity(message, securityClassInfo)
					.SetFortsMode(message.Volume == 0 ? 0 : 1)
					.SetFirstOrderId(message.OldOrderId.Value)
					.SetFirstOrderPrice(message.Price)
					.SetFirstVolume((int)message.Volume);
		}

		public static Transaction CreateRegisterTransaction(this OrderRegisterMessage message, string orderAccount, IDictionary<string, RefPair<SecurityTypes, string>> securityClassInfo, bool singleSlash)
		{
			if (securityClassInfo == null)
				throw new ArgumentNullException(nameof(securityClassInfo));

			if (message == null)
				throw new ArgumentNullException(nameof(message));

			var board = ExchangeBoard.GetOrCreateBoard(message.SecurityId.BoardCode);
			var needDepoAccount = board.IsMicex || board.IsUxStock;

			if (needDepoAccount)
			{
				if (orderAccount.IsEmpty())
					throw new ArgumentException(LocalizedStrings.Str1848Params.Put(message.PortfolioName));
			}
			else
				orderAccount = message.PortfolioName;

			var transaction = new Transaction(TransactionTypes.Register, message);

			transaction
				.SetAccount(orderAccount)
				.SetSecurity(message, securityClassInfo)
				.SetVolume((int)message.Volume);

			//20-ти символьное составное поле, может содержать код клиента и текстовый комментарий с тем же разделителем, что и при вводе заявки вручную.
			//для ртс код клиента не обязателен
			var clientCode = needDepoAccount ? message.PortfolioName : string.Empty;
			if (!message.Comment.IsEmpty())
			{
				// http://www.quik.ru/forum/import/24383
				//var splitter = message.PortfolioName.Contains("/") ? "//" : "/";

				// https://forum.quik.ru/forum10/topic1218/
				var splitter = singleSlash ? "/" : "//";

				clientCode = clientCode.IsEmpty() ? message.Comment : "{0}{1}{2}".Put(clientCode, splitter, message.Comment);

				if (clientCode.Length > 20)
					clientCode = clientCode.Remove(20);
			}

			if (!clientCode.IsEmpty())
				transaction.SetClientCode(clientCode);

			transaction.SetExpiryDate(message.TillDate);

			if (message.VisibleVolume != null && message.VisibleVolume != message.Volume)
			{
				return transaction
						.SetAction(TransactionActions.Iceberg)
						.SetVisibleVolume((int)message.VisibleVolume)

						.SetInstruction("Цена", transaction.GetInstruction(Transaction.Price))
						.SetInstruction("К/П", message.Side == Sides.Buy ? "Купля" : "Продажа")
						.SetInstruction("Тип", "Лимитная")
						.SetInstruction("Тип по цене", "по разным ценам")
						.SetInstruction("Тип по остатку", "поставить в очередь")
						.SetInstruction("Тип ввода значения цены", "По цене")

						.SetInstruction("Торговый счет", transaction.GetInstruction(Transaction.Account))
						.RemoveInstruction(Transaction.Account)

						.SetInstruction("Инструмент", transaction.GetInstruction(Transaction.SecurityCode))
						.RemoveInstruction(Transaction.SecurityCode)

						.SetInstruction("Лоты", transaction.GetInstruction(Transaction.Volume))
						.RemoveInstruction(Transaction.Volume)

						.SetInstruction("Примечание", transaction.GetInstruction(Transaction.ClientCode))
						.RemoveInstruction(Transaction.ClientCode);
			}

			switch (message.OrderType)
			{
				case OrderTypes.Market:
				case OrderTypes.Limit:
					transaction
						.SetSide(message.Side)
						.SetType(message.OrderType)
						.SetAction(TransactionActions.NewOrder)
						.SetPrice(message.Price);

					var tif = message.TimeInForce ?? TimeInForce.PutInQueue;

					if (!(message.SecurityId.SecurityType == SecurityTypes.Future && tif == TimeInForce.PutInQueue))
						transaction.SetTimeInForce(tif);

					break;
				case OrderTypes.Conditional:
				{
					var condition = (QuikOrderCondition)message.Condition;

					if (condition.ConditionOrderId != null && condition.ConditionOrderSide != null)
						message.Side = condition.ConditionOrderSide.Value.Invert();

					transaction
						.SetSide(message.Side)
						.SetAction(TransactionActions.NewStopOrder)
						.SetStopPrice(condition.StopPrice ?? 0);

					if (message.Price != 0)
						transaction.SetPrice(message.Price);

					string stopOrderKind;
					string stopPriceCondition;

					switch (condition.Type)
					{
						case QuikOrderConditionTypes.LinkedOrder:
							stopOrderKind = TransactionStopOrderKinds.WithLinkedLimitOrder;
							transaction
								.SetLinkedOrderPrice(condition.LinkedOrderPrice ?? 0)
								.SetLinkedOrderCancel(condition.LinkedOrderCancel ?? false);

							stopPriceCondition = message.Side == Sides.Buy ? ">=" : "<=";
							break;
						case QuikOrderConditionTypes.OtherSecurity:
							var otherSec = condition.OtherSecurityId;

							if (otherSec == null)
								throw new ArgumentException();

							stopOrderKind = TransactionStopOrderKinds.ConditionPriceByOtherSecurity;
							transaction.SetOtherSecurity((SecurityId)otherSec, securityClassInfo);

							stopPriceCondition = condition.StopPriceCondition == QuikStopPriceConditions.MoreOrEqual ? ">=" : "<=";
							break;
						case QuikOrderConditionTypes.StopLimit:
							stopPriceCondition = null;
							stopOrderKind = null;
							break;
						case QuikOrderConditionTypes.TakeProfit:
							stopPriceCondition = null;
							stopOrderKind = TransactionStopOrderKinds.TakeProfit;
							break;
						case QuikOrderConditionTypes.TakeProfitStopLimit:
							stopPriceCondition = null;
							stopOrderKind = TransactionStopOrderKinds.TakeProfitAndStopLimit;

							if (condition.ActiveTime != null)
							{
								transaction
									.SetIsActiveInTime(true)
									.SetActiveFrom(condition.ActiveTime.Min.UtcDateTime)
									.SetActiveTo(condition.ActiveTime.Max.UtcDateTime);
							}
							else
							{
								// http://stocksharp.com/forum/yaf_postsm25524_Nie-rabotaiet-razmieshchieniie-ordierov-po-ispolnieniiu.aspx
								// IS_ACTIVE_IN_TIME	Признак действия заявки типа «Тэйк-профит и стоп-лимит»
								if (condition.ConditionOrderId == null)
									transaction.SetIsActiveInTime(false);
							}

							if (condition.IsMarketTakeProfit != null)
								transaction.SetMarketTakeProfit((bool)condition.IsMarketTakeProfit);

							if (condition.IsMarketStopLimit != null)
								transaction.SetMarketStopLimit((bool)condition.IsMarketStopLimit);

							if (condition.StopLimitPrice != null)
								transaction.SetStopLimitPrice((decimal)condition.StopLimitPrice);

							break;
						default:
							throw new ArgumentOutOfRangeException();
					}

					if (condition.ConditionOrderId != null)
					{
						if (stopOrderKind.IsEmpty())
							stopOrderKind = TransactionStopOrderKinds.SimpleStopLimit;

						stopOrderKind = TransactionStopOrderKinds.ActivatedByOrder + stopOrderKind;

						transaction
							.SetConditionOrderId((long)condition.ConditionOrderId)
							.SetConditionOrderUseMatchedBalance(condition.ConditionOrderUseMatchedBalance ?? false)
							.SetConditionOrderPartiallyMatched(condition.ConditionOrderPartiallyMatched ?? false);
					}

					if (condition.Type == QuikOrderConditionTypes.TakeProfit || condition.Type == QuikOrderConditionTypes.TakeProfitStopLimit)
					{
						if (condition.Offset != null)
							transaction.SetOffset(condition.Offset);

						if (condition.Spread != null)
							transaction.SetSpread(condition.Spread);
					}

					if (!stopOrderKind.IsEmpty())
						transaction.SetStopOrderKind(stopOrderKind);

					if (!stopPriceCondition.IsEmpty())
						transaction.SetStopPriceCondition(stopPriceCondition);

					break;
				}
				case OrderTypes.Repo:
				{
					var orderInfo = message.RepoInfo;

					transaction
						.SetAction(TransactionActions.NewRepoNegDeal)
						.SetSide(message.Side)
						.SetPrice(message.Price);

					if (orderInfo.BlockSecurities != null)
						transaction.SetBlockSecurities((bool)orderInfo.BlockSecurities);

					if (orderInfo.Partner != null)
						transaction.SetPartner(orderInfo.Partner);

					if (orderInfo.MatchRef != null)
						transaction.SetMatchRef(orderInfo.MatchRef);

					if (orderInfo.RefundRate != null)
						transaction.SetRefundRate((int)orderInfo.RefundRate);

					if (orderInfo.Rate != null)
						transaction.SetRepoRate((int)orderInfo.Rate);

					if (orderInfo.Value != null)
						transaction.SetRepoValue((decimal)orderInfo.Value);

					if (orderInfo.Term != null)
						transaction.SetRepoTerm((int)orderInfo.Term);

					if (orderInfo.SecondPrice != null)
						transaction.SetSecondPrice((decimal)orderInfo.SecondPrice);

					if (orderInfo.SettleCode != null)
						transaction.SetSettleCode(orderInfo.SettleCode);

					if (orderInfo.SettleDate != null)
						transaction.SetSettleDate(orderInfo.SettleDate.Value.ToLocalTime(TimeHelper.Moscow));

					if (orderInfo.LowerDiscount != null)
						transaction.SetLowerDiscount((int)orderInfo.LowerDiscount);

					if (orderInfo.StartDiscount != null)
						transaction.SetStartDiscount((int)orderInfo.StartDiscount);

					if (orderInfo.UpperDiscount != null)
						transaction.SetUpperDiscount((int)orderInfo.UpperDiscount);

					break;
				}
				case OrderTypes.Rps:
				{
					var orderInfo = message.RpsInfo;

					transaction
						.SetAction(TransactionActions.NewNegDeal)
						.SetSide(message.Side)
						.SetPrice(message.Price);

					if (orderInfo.Partner != null)
						transaction.SetPartner(orderInfo.Partner);

					if (orderInfo.MatchRef != null)
						transaction.SetMatchRef(orderInfo.MatchRef);

					if (orderInfo.SettleCode != null)
						transaction.SetSettleCode(orderInfo.SettleCode);

					if (orderInfo.SettleDate != null)
						transaction.SetSettleDate(orderInfo.SettleDate.Value.ToLocalTime(TimeHelper.Moscow));

					if (orderInfo.ForAccount != null)
						transaction.SetForAccount(orderInfo.ForAccount);

					transaction.SetCurrencyCode(orderInfo.CurrencyType.ToMicexCurrencyName());

					break;
				}
				default:
					throw new NotSupportedException(LocalizedStrings.Str1849Params.Put(message.Type));
			}

			return transaction;
		}

		public static Transaction CreateCancelTransaction(this OrderCancelMessage message, IDictionary<string, RefPair<SecurityTypes, string>> securityClassInfo)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			var transaction = new Transaction(TransactionTypes.Cancel, message);

			transaction.SetSecurity(message, securityClassInfo);

			string action;
			Func<long, Transaction> idSetterFunc = transaction.SetOrderId;

			switch (message.OrderType)
			{
				case OrderTypes.Limit:
				case OrderTypes.Market:
					action = TransactionActions.KillOrder;
					break;
				case OrderTypes.Conditional:
					action = TransactionActions.KillStopOrder;
					idSetterFunc = transaction.SetStopOrderId;
					break;
				case OrderTypes.Repo:
				case OrderTypes.ExtRepo:
				case OrderTypes.Rps:
					action = TransactionActions.KillNegDeal;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(message), message.Type, LocalizedStrings.Str1600);
			}

			if (message.OrderId == null)
				throw new InvalidOperationException(LocalizedStrings.Str2252Params.Put(message.OrderTransactionId));

			idSetterFunc(message.OrderId.Value).SetAction(action);

			return transaction;
		}

		public static Transaction CreateCancelFuturesTransaction(this OrderGroupCancelMessage message, IDictionary<string, RefPair<SecurityTypes, string>> securityClassInfo)
		{
			if (message == null) 
				throw new ArgumentNullException(nameof(message));

			if (message.PortfolioName.IsEmpty())
				throw new ArgumentException("message");

			if (message.SecurityId.SecurityCode.IsEmpty())
				throw new ArgumentException("message");

			var underlyingSecurityCode = message.GetValue<string>("UnderlyingSecurityCode");

			if (underlyingSecurityCode.IsEmpty())
				throw new ArgumentException(LocalizedStrings.Str1850, nameof(message));

			var secType = message.SecurityId.SecurityType;

			if (secType != SecurityTypes.Future)
				throw new ArgumentException(LocalizedStrings.Str1851Params.Put(secType), nameof(message));

			var transaction = new Transaction(TransactionTypes.CancelGroup, message);

			transaction
				.SetAccount(message.PortfolioName)
				.SetBaseContract(underlyingSecurityCode)
				.SetAction(TransactionActions.KillAllFuturesOrders)
				.SetClassCode(securityClassInfo.GetSecurityClass(message.SecurityId));

			if (message.Side != null)
				transaction.SetSide(message.Side.Value);

			return transaction;
		}

		public static long TryGetTransactionId(this Transaction transaction)
		{
			if (transaction.TransactionType != TransactionTypes.Register && transaction.TransactionType != TransactionTypes.ReRegister)
				return 0;
		
			var orderRegisterMessage = transaction.Message as OrderRegisterMessage;

			return orderRegisterMessage != null ? orderRegisterMessage.TransactionId : 0;
		}

		private static Transaction SetSecurity(this Transaction transaction, OrderMessage message, IDictionary<string, RefPair<SecurityTypes, string>> securityClassInfo)
		{
			return
				transaction
					.SetClassCode(securityClassInfo.GetSecurityClass(message.SecurityId))
					.SetSecurityCode(message.SecurityId.SecurityCode);
		}

		private static Transaction SetOtherSecurity(this Transaction transaction, SecurityId securityId, IDictionary<string, RefPair<SecurityTypes, string>> securityClassInfo)
		{
			var secClass = securityClassInfo.GetSecurityClass(securityId);
			return transaction.SetOtherSecurity(securityId.SecurityCode, secClass);
		}

		//public static TransactionBuilder CreateCancelOrdersTransaction(long transactionId, bool isStopOrder, string account, OrderDirections? direction, string classCode, Security security)
		//{
		//    var builder = new TransactionBuilder(TransactionTypes.CancelGroup);

		//    transactionId.CheckTransactionId();

		//    builder.SetTransactionId(transactionId);
		//    builder.SetAction(isStopOrder ? TansactionActions.KillAllStopOrders : TansactionActions.KillAllOrders);

		//    if (!account.IsEmpty())
		//        builder.SetAccount(account);

		//    if (direction != null)
		//        builder.SetOperation((OrderDirections)direction);

		//    if (classCode != null)
		//        builder.SetClassCode(classCode);

		//    if (security != null)
		//        builder.SetSecurityCode(security);

		//    return builder;
		//}

		public static bool IfFOKCancelMessage(string message)
		{
			return message.ContainsIgnoreCase("Неполное сведение FOK заявки");
		}
	}
}