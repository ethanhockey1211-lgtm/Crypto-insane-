namespace TradingScanner.Tests.Fixtures;

/// <summary>
/// Message shapes taken from the Coinbase Exchange WebSocket documentation
/// (https://docs.cdp.coinbase.com/exchange/docs/websocket-channels). These are the contract the parser is tested against.
/// </summary>
public static class CoinbaseFixtures
{
    public const string Match =
        """{"type":"match","trade_id":10,"sequence":50,"maker_order_id":"ac928c66-ca53-498f-9c13-a110027a60e8","taker_order_id":"132fb6ae-456b-4654-b4e0-d681ac05cea1","time":"2014-11-07T08:19:27.028459Z","product_id":"BTC-USD","size":"5.23512","price":"400.23","side":"sell"}""";

    public const string MatchMakerBuy =
        """{"type":"match","trade_id":11,"sequence":51,"maker_order_id":"a","taker_order_id":"b","time":"2014-11-07T08:19:27.5Z","product_id":"BTC-USD","size":"0.5","price":"400.10","side":"buy"}""";

    public const string LastMatch =
        """{"type":"last_match","trade_id":9,"sequence":49,"maker_order_id":"a","taker_order_id":"b","time":"2014-11-07T08:19:26Z","product_id":"BTC-USD","size":"1","price":"400.00","side":"sell"}""";

    public const string Ticker =
        """{"type":"ticker","sequence":37475248783,"product_id":"ETH-USD","price":"1285.22","open_24h":"1310.79","volume_24h":"245532.79269678","low_24h":"1280.52","high_24h":"1313.8","volume_30d":"9788783.60117027","best_bid":"1285.04","best_bid_size":"0.46688654","best_ask":"1285.27","best_ask_size":"1.56637040","side":"buy","time":"2022-10-19T23:28:22.061769Z","trade_id":370843401,"last_size":"11.4396987"}""";

    public const string Heartbeat =
        """{"type":"heartbeat","sequence":90,"last_trade_id":20,"product_id":"BTC-USD","time":"2014-11-07T08:19:28.464459Z"}""";

    public const string Subscriptions =
        """{"type":"subscriptions","channels":[{"name":"matches","product_ids":["BTC-USD","ETH-USD"]},{"name":"ticker","product_ids":["BTC-USD","ETH-USD"]},{"name":"heartbeat","product_ids":["BTC-USD","ETH-USD"]}]}""";

    public const string Error =
        """{"type":"error","message":"Failed to subscribe","reason":"BTC-USDX is not a valid product"}""";

    public const string Status =
        """{"type":"status","products":[{"id":"BTC-USD","base_currency":"BTC","quote_currency":"USD","status":"online"}],"currencies":[]}""";

    public static string MatchAt(string productId, long tradeId, string time, string price, string size, string makerSide = "sell") =>
        $$"""{"type":"match","trade_id":{{tradeId}},"sequence":{{tradeId * 3}},"maker_order_id":"m","taker_order_id":"t","time":"{{time}}","product_id":"{{productId}}","size":"{{size}}","price":"{{price}}","side":"{{makerSide}}"}""";

    public static string SubscriptionsFor(params string[] products)
    {
        var ids = string.Join(",", products.Select(p => $"\"{p}\""));
        return $$"""{"type":"subscriptions","channels":[{"name":"matches","product_ids":[{{ids}}]},{"name":"ticker","product_ids":[{{ids}}]},{"name":"heartbeat","product_ids":[{{ids}}]}]}""";
    }

    public const string Products =
        """
        [
          {"id":"BTC-USD","base_currency":"BTC","quote_currency":"USD","quote_increment":"0.01","base_increment":"0.00000001","display_name":"BTC-USD","status":"online","trading_disabled":false},
          {"id":"ETH-USD","base_currency":"ETH","quote_currency":"USD","quote_increment":"0.01","base_increment":"0.00000001","display_name":"ETH-USD","status":"online","trading_disabled":false},
          {"id":"SOL-USD","base_currency":"SOL","quote_currency":"USD","quote_increment":"0.01","base_increment":"0.001","display_name":"SOL-USD","status":"online","trading_disabled":false},
          {"id":"ETH-BTC","base_currency":"ETH","quote_currency":"BTC","quote_increment":"0.00001","base_increment":"0.00000001","display_name":"ETH-BTC","status":"online","trading_disabled":false},
          {"id":"USDT-USD","base_currency":"USDT","quote_currency":"USD","quote_increment":"0.0001","base_increment":"0.01","display_name":"USDT-USD","status":"online","trading_disabled":false},
          {"id":"OLD-USD","base_currency":"OLD","quote_currency":"USD","quote_increment":"0.01","base_increment":"0.01","display_name":"OLD-USD","status":"delisted","trading_disabled":true},
          {"id":"HALT-USD","base_currency":"HALT","quote_currency":"USD","quote_increment":"0.01","base_increment":"0.01","display_name":"HALT-USD","status":"online","trading_disabled":true}
        ]
        """;

    public static string Stats(string volume, string last) =>
        $$"""{"open":"1.0","high":"2.0","low":"0.5","volume":"{{volume}}","last":"{{last}}","volume_30day":"1000"}""";
}
