﻿using NBitcoin;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace MagicalCryptoWallet.Converters
{
	/// <summary>
	/// Converter used to convert <see cref="Network"/> to and from JSON.
	/// </summary>
	/// <seealso cref="Newtonsoft.Json.JsonConverter" />
	public class NetworkConverter : JsonConverter
	{
		/// <inheritdoc />
		public override bool CanConvert(Type objectType)
		{
			return objectType == typeof(Network);
		}

		/// <inheritdoc />
		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
		{
			// check additional strings those are not checked by GetNetwork
			string networkString = ((string)reader.Value).Trim();
			if ("regression".Equals(networkString, StringComparison.OrdinalIgnoreCase))
				return Network.RegTest;

			return Network.GetNetwork(networkString);
		}

		/// <inheritdoc />
		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
		{
			writer.WriteValue(((Network)value).ToString());
		}
	}
}
