//! # NeoOnEdge Swap Router Contract
//!
//! WAX blockchain DEX aggregator — routes multi-hop token swaps across
//! Alcor v2, Alcor DEX, Defibox, Taco, Adex, NeftyBlocks, and WaxFusion
//! in a single atomic transaction.
//!
//! ## Memo format
//! ```
//! swap,<min_return>,<TICKER>@<contract>|<PAIR_ID>@<src>[-<PAIR_ID>@<src>...]
//! ```
//!
//! ## DEX source codes
//! | Code | Platform      | Contract     |
//! |------|---------------|--------------|
//! | a2   | Alcor v2      | swap.alcor   |
//! | ad   | Alcor DEX OB  | alcordexmain |
//! | d    | Defibox       | swap.box     |
//! | t    | Taco          | swap.taco    |
//! | a    | Adex          | swap.adex    |
//! | n    | NeftyBlocks   | swap.nefty   |
//! | wf   | WaxFusion     | dapp.fusion  |

#![no_std]
#![no_main]

extern crate alloc;
use alloc::vec::Vec;
use alloc::string::String;

use eosio_contract::*;

// ─────────────────────────────────────────────────────────────────────────────
// Constants
// ─────────────────────────────────────────────────────────────────────────────

const DEFAULT_FEE_BPS: u32 = 30;
const DEFAULT_FEE_WALLET: Name = n!("fee.neo");
const ADMIN_ACCOUNT: Name = n!("swap.neo");

fn is_swap_callback_sender(account: Name) -> bool {
    account == n!("swap.alcor") ||
    account == n!("alcordexmain") ||
    account == n!("swap.box") ||
    account == n!("swap.taco") ||
    account == n!("swap.adex") ||
    account == n!("swap.nefty") ||
    account == n!("dapp.fusion") ||
    account == n!("token.fusion")
}

// ─────────────────────────────────────────────────────────────────────────────
// Compatibility helpers for eosio-core 1.0.0 from the Nexus registry
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Clone, Copy, PartialEq, Eq, Default)]
pub struct SymbolCode(u64);

impl SymbolCode {
    pub const fn new(raw: u64) -> Self { Self(raw) }
    pub const fn raw(&self) -> u64 { self.0 }

    pub fn from_str(s: &str) -> Self {
        let bytes = s.as_bytes();
        let mut raw = 0u64;
        let len = if bytes.len() > 7 { 7 } else { bytes.len() };
        let mut i = 0;
        while i < len {
            raw |= (bytes[i] as u64) << (8 * i);
            i += 1;
        }
        Self(raw)
    }
}

impl Pack for SymbolCode {
    fn pack_size(&self) -> usize { 8 }
    fn try_pack(&self, ds: &mut DataStream) -> SerializeResult<()> { self.0.try_pack(ds) }
}

impl Unpack for SymbolCode {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> { Ok(Self(u64::try_unpack(ds)?)) }
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub struct ExtendedSymbol {
    pub sym: Symbol,
    pub contract: Name,
}

impl Pack for ExtendedSymbol {
    fn pack_size(&self) -> usize { 16 }
    fn try_pack(&self, ds: &mut DataStream) -> SerializeResult<()> {
        self.sym.try_pack(ds)?;
        self.contract.try_pack(ds)
    }
}

impl Unpack for ExtendedSymbol {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        Ok(Self { sym: Symbol::try_unpack(ds)?, contract: Name::try_unpack(ds)? })
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub struct ExtendedAsset {
    pub quantity: Asset,
    pub contract: Name,
}

impl Pack for ExtendedAsset {
    fn pack_size(&self) -> usize { 24 }
    fn try_pack(&self, ds: &mut DataStream) -> SerializeResult<()> {
        self.quantity.try_pack(ds)?;
        self.contract.try_pack(ds)
    }
}

impl Unpack for ExtendedAsset {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        Ok(Self { quantity: Asset::try_unpack(ds)?, contract: Name::try_unpack(ds)? })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Owned on-chain tables
// ─────────────────────────────────────────────────────────────────────────────

/// Global config — fee rate and fee wallet.
#[derive(Pack, Unpack, Clone)]
pub struct Config {
    pub fee: u32,
    pub fee_wallet: Name,
}

/// Concrete config row. Primary key is always 0.
#[derive(Pack, Unpack, Clone)]
pub struct ConfigRow {
    pub id: u64,
    pub fee: u32,
    pub fee_wallet: Name,
}

impl TableRow for ConfigRow {
    fn primary_key(&self) -> u64 { self.id }
}

/// Anti-re-entry discard list entry.
pub struct DiscardAccount {
    pub account: Name,
}

impl Pack for DiscardAccount {
    fn pack_size(&self) -> usize { 8 }
    fn try_pack(&self, ds: &mut DataStream) -> SerializeResult<()> {
        self.account.try_pack(ds)
    }
}
impl Unpack for DiscardAccount {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        Ok(Self { account: Name::try_unpack(ds)? })
    }
}
impl TableRow for DiscardAccount {
    fn primary_key(&self) -> u64 { self.account.raw() }
}

// ─────────────────────────────────────────────────────────────────────────────
// External DEX table mirrors — partial-decode structs
// (custom Unpack reads only required fields, then skips remaining bytes with
// try_advance(remaining()) so PrimaryTable::find's ensure_finished() passes)
// ─────────────────────────────────────────────────────────────────────────────

/// Alcor v2 pool (swap.alcor "pools").
pub struct AlcorPool {
    pub id: u64,
    pub active: bool,
    pub token_a: ExtendedAsset,
    pub token_b: ExtendedAsset,
}
impl Pack for AlcorPool {
    fn pack_size(&self) -> usize { 0 }
    fn try_pack(&self, _: &mut DataStream) -> SerializeResult<()> { Ok(()) }
}
impl Unpack for AlcorPool {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        let id = u64::try_unpack(ds)?;
        let active = bool::try_unpack(ds)?;
        let token_a = ExtendedAsset::try_unpack(ds)?;
        let token_b = ExtendedAsset::try_unpack(ds)?;
        ds.try_advance(ds.remaining())?;
        Ok(Self { id, active, token_a, token_b })
    }
}
impl TableRow for AlcorPool { fn primary_key(&self) -> u64 { self.id } }

/// Alcor DEX order-book market (alcordexmain "markets").
pub struct AlcorMarket {
    pub id: u64,
    pub base_token: ExtendedSymbol,
    pub quote_token: ExtendedSymbol,
}
impl Pack for AlcorMarket {
    fn pack_size(&self) -> usize { 0 }
    fn try_pack(&self, _: &mut DataStream) -> SerializeResult<()> { Ok(()) }
}
impl Unpack for AlcorMarket {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        let id = u64::try_unpack(ds)?;
        let base_token = ExtendedSymbol::try_unpack(ds)?;
        let quote_token = ExtendedSymbol::try_unpack(ds)?;
        ds.try_advance(ds.remaining())?;
        Ok(Self { id, base_token, quote_token })
    }
}
impl TableRow for AlcorMarket { fn primary_key(&self) -> u64 { self.id } }

/// Token definition used inside Defibox pair rows.
pub struct TokenDef { pub contract: Name, pub symbol: Symbol }
impl Pack for TokenDef {
    fn pack_size(&self) -> usize { 16 }
    fn try_pack(&self, ds: &mut DataStream) -> SerializeResult<()> {
        self.contract.try_pack(ds)?; self.symbol.try_pack(ds)
    }
}
impl Unpack for TokenDef {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        Ok(Self { contract: Name::try_unpack(ds)?, symbol: Symbol::try_unpack(ds)? })
    }
}

/// Defibox pair (swap.box "pairs").
pub struct DefiboxPair {
    pub id: u64,
    pub token0: TokenDef, pub token1: TokenDef,
    pub reserve0: Asset,  pub reserve1: Asset,
}
impl Pack for DefiboxPair {
    fn pack_size(&self) -> usize { 0 }
    fn try_pack(&self, _: &mut DataStream) -> SerializeResult<()> { Ok(()) }
}
impl Unpack for DefiboxPair {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        let id = u64::try_unpack(ds)?;
        let token0 = TokenDef::try_unpack(ds)?;
        let token1 = TokenDef::try_unpack(ds)?;
        let reserve0 = Asset::try_unpack(ds)?;
        let reserve1 = Asset::try_unpack(ds)?;
        ds.try_advance(ds.remaining())?;
        Ok(Self { id, token0, token1, reserve0, reserve1 })
    }
}
impl TableRow for DefiboxPair { fn primary_key(&self) -> u64 { self.id } }

/// Taco pair (swap.taco "pairs").
pub struct TacoPair {
    pub id: SymbolCode,
    pub supply: Asset,
    pub pool1: ExtendedAsset,
    pub pool2: ExtendedAsset,
}
impl Pack for TacoPair {
    fn pack_size(&self) -> usize { 0 }
    fn try_pack(&self, _: &mut DataStream) -> SerializeResult<()> { Ok(()) }
}
impl Unpack for TacoPair {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        let id = SymbolCode::try_unpack(ds)?;
        let supply = Asset::try_unpack(ds)?;
        let pool1 = ExtendedAsset::try_unpack(ds)?;
        let pool2 = ExtendedAsset::try_unpack(ds)?;
        ds.try_advance(ds.remaining())?;
        Ok(Self { id, supply, pool1, pool2 })
    }
}
impl TableRow for TacoPair { fn primary_key(&self) -> u64 { self.id.raw() } }

/// NeftyBlocks pair (swap.nefty "pairs").
pub struct NeftyPair {
    pub code: SymbolCode,
    pub active: bool,
    pub reserve0: ExtendedAsset,
    pub reserve1: ExtendedAsset,
}
impl Pack for NeftyPair {
    fn pack_size(&self) -> usize { 0 }
    fn try_pack(&self, _: &mut DataStream) -> SerializeResult<()> { Ok(()) }
}
impl Unpack for NeftyPair {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        let code = SymbolCode::try_unpack(ds)?;
        let active = bool::try_unpack(ds)?;
        let reserve0 = ExtendedAsset::try_unpack(ds)?;
        let reserve1 = ExtendedAsset::try_unpack(ds)?;
        ds.try_advance(ds.remaining())?;
        Ok(Self { code, active, reserve0, reserve1 })
    }
}
impl TableRow for NeftyPair { fn primary_key(&self) -> u64 { self.code.raw() } }

/// Adex pool (swap.adex "pools").
pub struct AdexPool {
    pub id: u64,
    pub code: SymbolCode,
    pub base_token: ExtendedAsset,
    pub quote_token: ExtendedAsset,
}
impl Pack for AdexPool {
    fn pack_size(&self) -> usize { 0 }
    fn try_pack(&self, _: &mut DataStream) -> SerializeResult<()> { Ok(()) }
}
impl Unpack for AdexPool {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        let id = u64::try_unpack(ds)?;
        let code = SymbolCode::try_unpack(ds)?;
        let base_token = ExtendedAsset::try_unpack(ds)?;
        let quote_token = ExtendedAsset::try_unpack(ds)?;
        ds.try_advance(ds.remaining())?;
        Ok(Self { id, code, base_token, quote_token })
    }
}
impl TableRow for AdexPool { fn primary_key(&self) -> u64 { self.id } }

/// Standard token accounts table (any token contract, table "accounts").
pub struct TokenAccount { pub balance: Asset }
impl Pack for TokenAccount {
    fn pack_size(&self) -> usize { 16 }
    fn try_pack(&self, ds: &mut DataStream) -> SerializeResult<()> { self.balance.try_pack(ds) }
}
impl Unpack for TokenAccount {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        Ok(Self { balance: Asset::try_unpack(ds)? })
    }
}
impl TableRow for TokenAccount { fn primary_key(&self) -> u64 { self.balance.symbol.code_raw() } }

/// WaxFusion global singleton (dapp.fusion "global").
/// Only the first 3 Asset fields (48 bytes) are decoded; remaining bytes skipped.
pub struct WaxFusionGlobal {
    pub swax_currently_earning: Asset,
    pub swax_currently_backing_lswax: Asset,
    pub liquified_swax: Asset,
}
impl Pack for WaxFusionGlobal {
    fn pack_size(&self) -> usize { 0 }
    fn try_pack(&self, _: &mut DataStream) -> SerializeResult<()> { Ok(()) }
}
impl Unpack for WaxFusionGlobal {
    fn try_unpack(ds: &mut DataStream) -> SerializeResult<Self> {
        let swax_currently_earning = Asset::try_unpack(ds)?;
        let swax_currently_backing_lswax = Asset::try_unpack(ds)?;
        let liquified_swax = Asset::try_unpack(ds)?;
        ds.try_advance(ds.remaining())?;
        Ok(Self { swax_currently_earning, swax_currently_backing_lswax, liquified_swax })
    }
}
impl TableRow for WaxFusionGlobal { fn primary_key(&self) -> u64 { n!("global").raw() } }

// ─────────────────────────────────────────────────────────────────────────────
// Route data structures
// ─────────────────────────────────────────────────────────────────────────────

/// One hop in the route (packed into the `runstep` inline action chain).
#[derive(Pack, Unpack, Clone)]
pub struct PairSchema {
    pub pair_id: EosioString,
    pub src: EosioString,
}

/// Parsed memo.
pub struct MemoSchema {
    pub min_return: u64,
    pub ticker_return: SymbolCode,
    pub contract_return: Name,
    pub pair_ids: Vec<PairSchema>,
}

/// Normalised pool view for a single hop.
pub struct PoolSchema {
    pub pool1: ExtendedAsset,
    pub pool2: ExtendedAsset,
}

// ─────────────────────────────────────────────────────────────────────────────
// Contract
// ─────────────────────────────────────────────────────────────────────────────

pub struct NeoEdge {
    receiver: Name,
    /// For notifications: the account that sent the notification (token contract).
    /// For direct actions: equals receiver.
    code: Name,
    _action: Name,
}

impl NeoEdge {
    fn get_self(&self) -> Name { self.receiver }

    fn require_admin(&self) {
        require_auth_name(ADMIN_ACCOUNT);
    }

    fn config_table(&self) -> PrimaryTable<ConfigRow> {
        PrimaryTable::new(self.get_self(), self.get_self(), n!("config"))
    }

    fn discard_table(&self) -> PrimaryTable<DiscardAccount> {
        PrimaryTable::new(self.get_self(), self.get_self(), n!("discards"))
    }

    fn get_config(&self) -> Config {
        self.config_table()
            .find(0)
            .map(|row| Config { fee: row.fee, fee_wallet: row.fee_wallet })
            .unwrap_or(Config { fee: DEFAULT_FEE_BPS, fee_wallet: DEFAULT_FEE_WALLET })
    }

    fn set_config(&self, cfg: Config) {
        let table = self.config_table();
        let row = ConfigRow { id: 0, fee: cfg.fee, fee_wallet: cfg.fee_wallet };
        if table.exists(row.id) { table.modify(ADMIN_ACCOUNT, &row); }
        else { table.emplace(ADMIN_ACCOUNT, &row); }
    }

    fn is_discarded(&self, account: Name) -> bool {
        self.discard_table().exists(account.raw())
    }

    fn get_balance(&self, token_contract: Name, owner: Name, sym_code: SymbolCode) -> i64 {
        let t = PrimaryTable::<TokenAccount>::new(token_contract, owner, n!("accounts"));
        t.find(sym_code.raw()).map(|r| r.balance.amount).unwrap_or(0)
    }

    fn mul_div(a: i64, b: i64, denom: i64) -> i64 {
        check(denom > 0, "mul_div: zero denominator");
        let r = (a as i128) * (b as i128) / (denom as i128);
        check(r >= i64::MIN as i128 && r <= i64::MAX as i128, "mul_div: overflow");
        r as i64
    }

    // ── Pool resolution ───────────────────────────────────────────────────────

    fn fetch_pool(&self, src: &str, pair_id_str: &str) -> PoolSchema {
        match src {
            "a2" => {
                let id = parse_u64(pair_id_str);
                let p = PrimaryTable::<AlcorPool>::new(n!("swap.alcor"), n!("swap.alcor"), n!("pools"))
                    .find(id);
                check(p.is_some(), "alcor v2 pool not found");
                let p = p.unwrap();
                PoolSchema {
                    pool1: ExtendedAsset { quantity: Asset::new(0, p.token_a.quantity.symbol), contract: p.token_a.contract },
                    pool2: ExtendedAsset { quantity: Asset::new(0, p.token_b.quantity.symbol), contract: p.token_b.contract },
                }
            }
            "ad" => {
                let id = parse_u64(pair_id_str);
                let m = PrimaryTable::<AlcorMarket>::new(n!("alcordexmain"), n!("alcordexmain"), n!("markets"))
                    .find(id);
                check(m.is_some(), "alcor dex market not found");
                let m = m.unwrap();
                PoolSchema {
                    pool1: ExtendedAsset { quantity: Asset::new(0, m.base_token.sym), contract: m.base_token.contract },
                    pool2: ExtendedAsset { quantity: Asset::new(0, m.quote_token.sym), contract: m.quote_token.contract },
                }
            }
            "d" => {
                let id = parse_u64(pair_id_str);
                let p = PrimaryTable::<DefiboxPair>::new(n!("swap.box"), n!("swap.box"), n!("pairs"))
                    .find(id);
                check(p.is_some(), "defibox pair not found");
                let p = p.unwrap();
                PoolSchema {
                    pool1: ExtendedAsset { quantity: p.reserve0, contract: p.token0.contract },
                    pool2: ExtendedAsset { quantity: p.reserve1, contract: p.token1.contract },
                }
            }
            "t" => {
                let key = SymbolCode::from_str(pair_id_str).raw();
                let p = PrimaryTable::<TacoPair>::new(n!("swap.taco"), n!("swap.taco"), n!("pairs"))
                    .find(key);
                check(p.is_some(), "taco pair not found");
                let p = p.unwrap();
                PoolSchema { pool1: p.pool1, pool2: p.pool2 }
            }
            "n" => {
                let key = SymbolCode::from_str(pair_id_str).raw();
                let p = PrimaryTable::<NeftyPair>::new(n!("swap.nefty"), n!("swap.nefty"), n!("pairs"))
                    .find(key);
                check(p.is_some(), "nefty pair not found");
                let p = p.unwrap();
                PoolSchema { pool1: p.reserve0, pool2: p.reserve1 }
            }
            "a" => {
                let id = parse_u64(pair_id_str);
                let p = PrimaryTable::<AdexPool>::new(n!("swap.adex"), n!("swap.adex"), n!("pools"))
                    .find(id);
                check(p.is_some(), "adex pool not found");
                let p = p.unwrap();
                PoolSchema { pool1: p.base_token, pool2: p.quote_token }
            }
            "wf" => {
                PoolSchema {
                    pool1: ExtendedAsset { quantity: Asset::new(0, sym!(8, "WAX")),   contract: n!("eosio.token") },
                    pool2: ExtendedAsset { quantity: Asset::new(0, sym!(8, "LSWAX")), contract: n!("token.fusion") },
                }
            }
            _ => { check(false, "unknown dex src"); unreachable!() }
        }
    }

    fn get_output_from_pool(send: &ExtendedAsset, pool: &PoolSchema) -> ExtendedAsset {
        let sc = send.quantity.symbol.code_raw();
        let ct = send.contract;
        if pool.pool1.quantity.symbol.code_raw() == sc && pool.pool1.contract == ct {
            ExtendedAsset { quantity: Asset::new(0, pool.pool2.quantity.symbol), contract: pool.pool2.contract }
        } else if pool.pool2.quantity.symbol.code_raw() == sc && pool.pool2.contract == ct {
            ExtendedAsset { quantity: Asset::new(0, pool.pool1.quantity.symbol), contract: pool.pool1.contract }
        } else {
            check(false, "send token not in pool"); unreachable!()
        }
    }

    // ── Swap dispatch ─────────────────────────────────────────────────────────

    fn process_swap(&self, send: &ExtendedAsset, receive: &ExtendedAsset, src: &str, pair_id: &str) {
        match src {
            "a2" => {
                let mut m = String::new();
                m.push_str("swapexactin#"); m.push_str(pair_id); m.push('#');
                push_name_str(&mut m, self.get_self()); m.push('#');
                push_zero_asset(&mut m, receive.quantity.symbol); m.push('@');
                push_name_str(&mut m, receive.contract); m.push_str("#0");
                self.send_token(send.contract, n!("swap.alcor"), send.quantity, &m);
            }
            "ad" => {
                let mut m = String::new();
                push_zero_asset(&mut m, receive.quantity.symbol); m.push('@');
                push_name_str(&mut m, receive.contract);
                self.send_token(send.contract, n!("alcordexmain"), send.quantity, &m);
            }
            "d" => {
                let mut m = String::new();
                m.push_str("swap,0,"); m.push_str(pair_id);
                self.send_token(send.contract, n!("swap.box"), send.quantity, &m);
            }
            "t" => {
                let mut m = String::new();
                push_zero_asset(&mut m, receive.quantity.symbol); m.push('@');
                push_name_str(&mut m, receive.contract);
                self.send_token(send.contract, n!("swap.taco"), send.quantity, &m);
            }
            "a" => {
                let mut m = String::new();
                m.push_str("swap:"); m.push_str(pair_id); m.push(';');
                self.send_token(send.contract, n!("swap.adex"), send.quantity, &m);
            }
            "n" => {
                let mut m = String::new();
                m.push_str("swap:"); m.push_str(pair_id);
                self.send_token(send.contract, n!("swap.nefty"), send.quantity, &m);
            }
            _ => { check(false, "unknown src in process_swap"); }
        }
    }

    fn process_liquid_staking(&self, send: &ExtendedAsset) {
        let mut stake_data = DataStream::new(64);
        self.get_self().pack(&mut stake_data);
        self.send_inline_action(n!("dapp.fusion"), n!("stake"), &stake_data);

        let g = PrimaryTable::<WaxFusionGlobal>::new(n!("dapp.fusion"), n!("dapp.fusion"), n!("global"))
            .find(n!("global").raw());
        check(g.is_some(), "waxfusion global not found");
        let g = g.unwrap();

        let is_wax = send.quantity.symbol.code_raw() == SymbolCode::from_str("WAX").raw();
        if is_wax {
            self.send_token(n!("eosio.token"), n!("dapp.fusion"), send.quantity, "stake");
            let swax_amount = send.quantity.amount;
            let min_lswax = Self::mul_div(
                g.liquified_swax.amount, send.quantity.amount, g.swax_currently_backing_lswax.amount,
            );
            let mut liquify_data = DataStream::new(64);
            self.get_self().pack(&mut liquify_data);
            Asset::new(swax_amount, sym!(8, "SWAX")).pack(&mut liquify_data);
            Asset::new(min_lswax, sym!(8, "LSWAX")).pack(&mut liquify_data);
            self.send_inline_action(n!("dapp.fusion"), n!("liquifyexact"), &liquify_data);
        } else {
            let swax_amount = Self::mul_div(
                g.swax_currently_backing_lswax.amount, send.quantity.amount, g.liquified_swax.amount,
            );
            let mut memo = String::new();
            memo.push_str("|unliquify_exact|");
            push_i64_str(&mut memo, swax_amount);
            memo.push('|');
            self.send_token(n!("token.fusion"), n!("dapp.fusion"), send.quantity, &memo);
            let mut redeem_data = DataStream::new(64);
            self.get_self().pack(&mut redeem_data);
            Asset::new(swax_amount, sym!(8, "SWAX")).pack(&mut redeem_data);
            self.send_inline_action(n!("dapp.fusion"), n!("instaredeem"), &redeem_data);
        }
    }

    fn send_token(&self, token_contract: Name, to: Name, quantity: Asset, memo: &str) {
        let from = self.get_self();
        let memo_s = EosioString::from_str(memo);
        let mut data = DataStream::new(512);
        from.pack(&mut data);
        to.pack(&mut data);
        quantity.pack(&mut data);
        memo_s.pack(&mut data);
        self.send_inline_action(token_contract, n!("transfer"), &data);
    }

    fn send_inline_action(&self, account: Name, action_name: Name, data: &DataStream) {
        let self_name = self.get_self();
        let auth = PermissionLevel::active(self_name);
        let data_bytes = data.as_slice();

        let mut buf = DataStream::new(1024);
        account.pack(&mut buf);
        action_name.pack(&mut buf);
        pack_varuint32(&mut buf, 1);
        auth.pack(&mut buf);
        pack_varuint32(&mut buf, data_bytes.len() as u32);
        buf.write_bytes(data_bytes);

        let s = buf.as_slice();
        unsafe { eosio_core::send_inline(s.as_ptr(), s.len() as u32); }
    }

    // ── Swap chain ────────────────────────────────────────────────────────────

    fn start_swap_chain(
        &self, from: Name, first_send: ExtendedAsset,
        min_return: u64, ticker_return: SymbolCode, contract_return: Name,
        pair_ids: Vec<PairSchema>,
    ) {
        check(!pair_ids.is_empty(), "empty route");

        let config = self.get_config();
        let fee = if pair_ids.len() == 1 && pair_ids[0].src.as_str() == "ad" { 0i32 }
                  else { config.fee as i32 };

        let hop0 = &pair_ids[0];
        let pool = self.fetch_pool(hop0.src.as_str(), hop0.pair_id.as_str());
        let receive_ea = Self::get_output_from_pool(&first_send, &pool);
        let receive_sym = SymbolCode::new(receive_ea.quantity.symbol.code_raw());
        let bal_before = self.get_balance(receive_ea.contract, self.get_self(), receive_sym);
        let prev_ea = ExtendedAsset {
            quantity: Asset::new(bal_before, receive_ea.quantity.symbol),
            contract: receive_ea.contract,
        };

        if hop0.src.as_str() == "wf" {
            self.process_liquid_staking(&first_send);
        } else {
            self.process_swap(&first_send, &receive_ea, hop0.src.as_str(), hop0.pair_id.as_str());
        }

        let remaining: Vec<PairSchema> = pair_ids[1..].to_vec();
        let zero_diff = ExtendedAsset { quantity: Asset::new(0, receive_ea.quantity.symbol), contract: receive_ea.contract };
        self.schedule_runstep(from, min_return, ticker_return, contract_return, fee, zero_diff, prev_ea, remaining);
    }

    /// Build and dispatch `swap.neo::runstep` as an inline action.
    /// Uses the local varuint-safe inline dispatcher instead of InlineAction.
    fn schedule_runstep(
        &self, original_from: Name, min_return: u64, ticker_return: SymbolCode,
        contract_return: Name, fee: i32,
        send_balance_difference: ExtendedAsset, previous_bal: ExtendedAsset,
        pair_ids: Vec<PairSchema>,
    ) {
        let mut data = DataStream::new(768);
        original_from.pack(&mut data);
        min_return.pack(&mut data);
        ticker_return.pack(&mut data);
        contract_return.pack(&mut data);
        fee.pack(&mut data);
        send_balance_difference.pack(&mut data);
        previous_bal.pack(&mut data);
        pair_ids.pack(&mut data);

        self.send_inline_action(self.get_self(), n!("runstep"), &data);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Contract actions
// ─────────────────────────────────────────────────────────────────────────────

#[eosio_contract]
impl NeoEdge {
    pub fn new(receiver: Name, code: Name, action: Name) -> Self {
        Self { receiver, code, _action: action }
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    #[eosio_notify("*::transfer")]
    pub fn on_transfer(&self, from: Name, to: Name, quantity: Asset, memo: EosioString) {
        if to != self.get_self() { return; }
        if from == self.get_self() { return; }
        if is_swap_callback_sender(from) { return; }
        if memo.is_empty() { return; }
        if self.is_discarded(from) { return; }
        check(quantity.amount > 0, "transfer must be positive");

        // self.code == the notifying contract (the token contract)
        let token_contract = self.code;
        let send_ea = ExtendedAsset { quantity, contract: token_contract };
        let parsed = parse_memo(memo.as_str());

        self.start_swap_chain(
            from, send_ea, parsed.min_return,
            parsed.ticker_return, parsed.contract_return, parsed.pair_ids,
        );
    }

    // ── Recursive hop executor ────────────────────────────────────────────────

    #[eosio_action]
    pub fn runstep(
        &self,
        original_from: Name,
        min_return: u64,
        ticker_return: SymbolCode,
        contract_return: Name,
        fee: i32,
        _send_balance_difference: ExtendedAsset,
        previous_bal: ExtendedAsset,
        pair_ids: Vec<PairSchema>,
    ) {
        require_auth_name(self.get_self());

        let sym_code = SymbolCode::new(previous_bal.quantity.symbol.code_raw());
        let current_bal = self.get_balance(previous_bal.contract, self.get_self(), sym_code);
        let received = current_bal - previous_bal.quantity.amount;
        check(received > 0, "received zero from previous hop");

        let send_ea = ExtendedAsset {
            quantity: Asset::new(received, previous_bal.quantity.symbol),
            contract: previous_bal.contract,
        };

        if pair_ids.is_empty() {
            // Terminal hop: validate, deduct fee, deliver to user.
            check(send_ea.quantity.symbol.code_raw() == ticker_return.raw(), "output symbol mismatch");
            check(send_ea.contract == contract_return, "output contract mismatch");

            let config = self.get_config();
            let fee_bps = fee.max(0);
            let fee_amount = (send_ea.quantity.amount * fee_bps as i64) / 10_000;
            let net = send_ea.quantity.amount - fee_amount;
            check(net as u64 >= min_return, "slippage: below minimum");

            let swap_memo = EosioString::from_str("NeoOnEdge swap");
            let mut transfer_data = DataStream::new(256);
            self.get_self().pack(&mut transfer_data);
            original_from.pack(&mut transfer_data);
            Asset::new(net, send_ea.quantity.symbol).pack(&mut transfer_data);
            swap_memo.pack(&mut transfer_data);
            self.send_inline_action(send_ea.contract, n!("transfer"), &transfer_data);

            if fee_amount > 0 {
                let fee_memo = EosioString::from_str("NeoOnEdge fees");
                let fw = config.fee_wallet;
                let mut fee_data = DataStream::new(256);
                self.get_self().pack(&mut fee_data);
                fw.pack(&mut fee_data);
                Asset::new(fee_amount, send_ea.quantity.symbol).pack(&mut fee_data);
                fee_memo.pack(&mut fee_data);
                self.send_inline_action(send_ea.contract, n!("transfer"), &fee_data);
            }
        } else {
            // Intermediate hop: execute next DEX and recurse.
            let next = &pair_ids[0];
            let pool = self.fetch_pool(next.src.as_str(), next.pair_id.as_str());
            let receive_ea = Self::get_output_from_pool(&send_ea, &pool);
            let receive_sym = SymbolCode::new(receive_ea.quantity.symbol.code_raw());
            let bal_before = self.get_balance(receive_ea.contract, self.get_self(), receive_sym);
            let prev_ea = ExtendedAsset {
                quantity: Asset::new(bal_before, receive_ea.quantity.symbol),
                contract: receive_ea.contract,
            };

            if next.src.as_str() == "wf" {
                self.process_liquid_staking(&send_ea);
            } else {
                self.process_swap(&send_ea, &receive_ea, next.src.as_str(), next.pair_id.as_str());
            }

            let remaining: Vec<PairSchema> = pair_ids[1..].to_vec();
            let zero_diff = ExtendedAsset { quantity: Asset::new(0, receive_ea.quantity.symbol), contract: receive_ea.contract };
            self.schedule_runstep(original_from, min_return, ticker_return, contract_return, fee, zero_diff, prev_ea, remaining);
        }
    }

    // ── Brand marker ──────────────────────────────────────────────────────────

    #[eosio_action]
    pub fn madeonwoe(&self) {}

    // ── Admin actions ─────────────────────────────────────────────────────────

    #[eosio_action]
    pub fn setfee(&self, fee: u32) {
        self.require_admin();
        check(fee <= 100, "fee cannot exceed 100 bps");
        let mut cfg = self.get_config();
        cfg.fee = fee;
        self.set_config(cfg);
    }

    #[eosio_action]
    pub fn setfeewallet(&self, wallet: Name) {
        self.require_admin();
        check(is_account_name(wallet), "wallet does not exist");
        let mut cfg = self.get_config();
        cfg.fee_wallet = wallet;
        self.set_config(cfg);
    }

    #[eosio_action]
    pub fn adddiscard(&self, account: Name) {
        self.require_admin();
        let t = self.discard_table();
        if !t.exists(account.raw()) {
            t.emplace(ADMIN_ACCOUNT, &DiscardAccount { account });
        }
    }

    #[eosio_action]
    pub fn remdiscard(&self, account: Name) {
        self.require_admin();
        let t = self.discard_table();
        check(t.exists(account.raw()), "not in discard list");
        t.erase(account.raw());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Memo parsing
// ─────────────────────────────────────────────────────────────────────────────

fn parse_memo(memo: &str) -> MemoSchema {
    let mut parts = memo.splitn(3, ',');
    check(parts.next().unwrap_or("") == "swap", "memo must start with 'swap'");
    let min_return = parse_u64(parts.next().unwrap_or(""));
    let rest = parts.next().unwrap_or("");
    check(!rest.is_empty(), "empty route in memo");

    let pipe = rest.find('|');
    check(pipe.is_some(), "memo missing '|'");
    let pipe = pipe.unwrap();
    let output_str = &rest[..pipe];
    let hops_str   = &rest[pipe + 1..];

    let at = output_str.find('@');
    check(at.is_some(), "output token missing '@'");
    let at = at.unwrap();
    let ticker_return   = SymbolCode::from_str(&output_str[..at]);
    let contract_return = Name::from_str(&output_str[at + 1..]);

    let mut pair_ids: Vec<PairSchema> = Vec::new();
    for hop in hops_str.split('-') {
        if hop.is_empty() { continue; }
        let at = hop.rfind('@');
        check(at.is_some(), "hop missing '@'");
        let at = at.unwrap();
        pair_ids.push(PairSchema {
            pair_id: EosioString::from_str(&hop[..at]),
            src:     EosioString::from_str(&hop[at + 1..]),
        });
    }
    check(!pair_ids.is_empty(), "no hops in memo");

    MemoSchema { min_return, ticker_return, contract_return, pair_ids }
}

// ─────────────────────────────────────────────────────────────────────────────
// String helpers (no_std / no std::fmt)
// ─────────────────────────────────────────────────────────────────────────────

fn parse_u64(s: &str) -> u64 {
    let mut r: u64 = 0;
    for b in s.bytes() {
        check(b >= b'0' && b <= b'9', "invalid digit in number");
        r = r.saturating_mul(10).saturating_add((b - b'0') as u64);
    }
    r
}

fn pack_varuint32(ds: &mut DataStream, mut value: u32) {
    loop {
        let mut byte = (value & 0x7f) as u8;
        value >>= 7;
        if value > 0 { byte |= 0x80; }
        ds.write_byte(byte);
        if value == 0 { break; }
    }
}

fn push_name_str(s: &mut String, name: Name) {
    s.push_str(name.to_str().as_str());
}

fn push_zero_asset(s: &mut String, sym: Symbol) {
    let prec = sym.precision() as usize;
    s.push('0');
    if prec > 0 {
        s.push('.');
        for _ in 0..prec { s.push('0'); }
    }
    s.push(' ');
    s.push_str(sym.code().as_str());
}

fn push_i64_str(s: &mut String, n: i64) {
    if n < 0 { s.push('-'); push_u64_str(s, (-(n as i128)) as u64); }
    else { push_u64_str(s, n as u64); }
}

fn push_u64_str(s: &mut String, n: u64) {
    if n == 0 { s.push('0'); return; }
    let mut buf = [0u8; 20];
    let mut len = 0;
    let mut v = n;
    while v > 0 { buf[len] = b'0' + (v % 10) as u8; v /= 10; len += 1; }
    for i in (0..len).rev() { s.push(buf[i] as char); }
}
