-- Durable state. Complex records are stored as jsonb documents with the columns needed for lookup indexed.
create table if not exists alert_rules (
    id uuid primary key,
    name text not null,
    enabled boolean not null,
    symbol text null,
    created_at timestamptz not null,
    data jsonb not null
);

create table if not exists alert_events (
    id uuid primary key,
    rule_id uuid not null,
    at timestamptz not null,
    symbol text not null,
    data jsonb not null
);
create index if not exists alert_events_at on alert_events (at desc);

create table if not exists paper_accounts (
    id uuid primary key,
    created_at timestamptz not null,
    data jsonb not null
);

create table if not exists paper_orders (
    id uuid primary key,
    account_id uuid not null,
    symbol text not null,
    status text not null,
    created_at timestamptz not null,
    data jsonb not null
);
create index if not exists paper_orders_created on paper_orders (created_at desc);

create table if not exists paper_positions (
    id uuid primary key,
    account_id uuid not null,
    symbol text not null,
    status text not null,
    opened_at timestamptz not null,
    data jsonb not null
);
create index if not exists paper_positions_opened on paper_positions (opened_at desc);

create table if not exists signals (
    id uuid primary key,
    symbol text not null,
    at timestamptz not null,
    setup text not null,
    score double precision not null,
    complete boolean not null,
    config_version int not null,
    signal jsonb not null,
    outcome jsonb not null
);
create index if not exists signals_at on signals (at desc);
create index if not exists signals_symbol_at on signals (symbol, at desc);
create index if not exists signals_incomplete on signals (complete) where complete = false;

create table if not exists candles (
    symbol text not null,
    timeframe int not null,
    open_time timestamptz not null,
    open numeric not null,
    high numeric not null,
    low numeric not null,
    close numeric not null,
    volume numeric not null,
    quote_volume numeric not null,
    buy_volume numeric not null,
    sell_volume numeric not null,
    trade_count int not null,
    source smallint not null,
    primary key (symbol, timeframe, open_time)
);

-- TimescaleDB is optional: turn candles into a hypertable when the extension is installed.
do $$
begin
    if exists (select 1 from pg_extension where extname = 'timescaledb') then
        perform create_hypertable('candles', 'open_time', if_not_exists => true, migrate_data => true);
    end if;
end $$;
