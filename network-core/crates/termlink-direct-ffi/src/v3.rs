use std::{
    ffi::c_void,
    mem::size_of,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr},
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, Receiver, RecvTimeoutError, SyncSender, TrySendError},
    },
    thread,
    time::{Duration, Instant},
};

#[cfg(target_os = "android")]
use termlink_direct_nat::AndroidNetworkBinder;
use termlink_direct_nat::{DirectUdpSocket, MAX_STUN_SERVERS, validate_candidate_endpoint};
use termlink_direct_quic::TemporaryIdentity;

use crate::{
    ABI_VERSION_V2, EVENT_IDENTITY_READY_V2, EVENT_IGNORED_STALE_V2,
    MAX_IDENTITY_CERTIFICATE_BYTES_V2, MAX_SIGNATURE_BYTES_V2, MAX_V2_QUEUE_CAPACITY,
    PLATFORM_ANDROID_V2, PLATFORM_WINDOWS_V2, REASON_CANCELLED_V2, REASON_DISABLED_V2,
    REASON_INTERNAL_V2, REASON_INVALID_ABI_V2, REASON_INVALID_CANDIDATE_V2,
    REASON_INVALID_EPOCH_V2, REASON_INVALID_PLATFORM_V2, REASON_NETWORK_CHANGED_V2,
    REASON_QUEUE_FULL_V2, REASON_SIGNATURE_TIMEOUT_V2, REASON_SOCKET_V2, STATE_GATHERING_V2,
    STATE_RELAY_ONLY_V2, TermLinkDirectControlV2, TermLinkDirectEventV2, TermLinkDirectSignatureV2,
    TermLinkDirectStartV2, TermLinkSocketAddrV2,
};

pub const ABI_VERSION_V3: u32 = 3;
pub const EVENT_CANDIDATES_READY_V3: u32 = 7;
pub const MAX_CANDIDATES_V3: usize = 3;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectCreateV3 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub enabled: u32,
    pub queue_capacity: u32,
    pub platform: u32,
    pub reserved: [u32; 3],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TermLinkDirectGatherV3 {
    pub abi_version: u32,
    pub struct_size: u32,
    pub epoch: u64,
    pub attempt_id: [u8; 16],
    pub network_handle: u64,
    pub local_bind: TermLinkSocketAddrV2,
    pub advertised_local_ip: [u8; 16],
    pub advertised_local_ip_length: u32,
    pub stun_count: u32,
    pub stun_servers: [TermLinkSocketAddrV2; MAX_STUN_SERVERS],
    pub gather_timeout_ms: u32,
    pub reserved0: u32,
    pub reserved: [u64; 4],
}

impl Default for TermLinkDirectGatherV3 {
    fn default() -> Self {
        Self {
            abi_version: 0,
            struct_size: 0,
            epoch: 0,
            attempt_id: [0; 16],
            network_handle: 0,
            local_bind: TermLinkSocketAddrV2::default(),
            advertised_local_ip: [0; 16],
            advertised_local_ip_length: 0,
            stun_count: 0,
            stun_servers: [TermLinkSocketAddrV2::default(); MAX_STUN_SERVERS],
            gather_timeout_ms: 0,
            reserved0: 0,
            reserved: [0; 4],
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectNominateV3 {
    pub abi_version: u32,
    pub struct_size: u32,
    /// The embedded record remains the exact V2 nomination layout/header.
    pub start_v2: TermLinkDirectStartV2,
    /// HKDF output for datagrams whose sender is the local device.
    pub local_send_probe_hmac_key: [u8; 32],
    /// HKDF output for datagrams whose sender is the nominated peer.
    pub peer_receive_probe_hmac_key: [u8; 32],
    pub reserved: [u64; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TermLinkDirectCandidatesV3 {
    pub candidate_count: u32,
    pub reserved: u32,
    pub candidates: [TermLinkSocketAddrV2; MAX_CANDIDATES_V3],
}

impl Default for TermLinkDirectCandidatesV3 {
    fn default() -> Self {
        Self {
            candidate_count: 0,
            reserved: 0,
            candidates: [TermLinkSocketAddrV2::default(); MAX_CANDIDATES_V3],
        }
    }
}

enum CommandV3 {
    Gather(Box<TermLinkDirectGatherV3>),
    Nominate(Box<TermLinkDirectNominateV3>),
    Cancel(TermLinkDirectControlV2),
    NetworkChanged(TermLinkDirectControlV2),
}

struct RetainedSocketV3 {
    epoch: u64,
    attempt_id: [u8; 16],
    network_handle: u64,
    identity: TemporaryIdentity,
    socket: DirectUdpSocket,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct OwnerV3 {
    epoch: u64,
    attempt_id: [u8; 16],
}

impl OwnerV3 {
    fn matches(self, epoch: u64, attempt_id: [u8; 16]) -> bool {
        self.epoch == epoch && self.attempt_id == attempt_id
    }
}

pub struct NativeBridgeV3 {
    config: TermLinkDirectCreateV3,
    commands: SyncSender<CommandV3>,
    signatures: SyncSender<Box<TermLinkDirectSignatureV2>>,
    events: Arc<crate::v2::EventQueueV2>,
    owner: Arc<Mutex<OwnerV3>>,
    authoritative_epoch: Arc<AtomicU64>,
    cancel_epoch: Arc<AtomicU64>,
    network_changed_epoch: Arc<AtomicU64>,
    relay_only: Arc<AtomicBool>,
    #[cfg_attr(not(test), allow(dead_code))]
    worker_alive: Arc<AtomicBool>,
}

impl NativeBridgeV3 {
    fn create(config: TermLinkDirectCreateV3) -> Result<Self, ()> {
        if config.abi_version != ABI_VERSION_V3
            || config.struct_size as usize != size_of::<TermLinkDirectCreateV3>()
            || config.enabled > 1
            || !matches!(config.platform, PLATFORM_WINDOWS_V2 | PLATFORM_ANDROID_V2)
            || config.queue_capacity == 0
            || config.queue_capacity as usize > MAX_V2_QUEUE_CAPACITY
            || config.reserved != [0; 3]
        {
            return Err(());
        }
        let capacity = config.queue_capacity as usize;
        let (commands, command_receiver) = mpsc::sync_channel(capacity);
        let (signatures, signature_receiver) = mpsc::sync_channel(capacity);
        let events = Arc::new(crate::v2::EventQueueV2::new(capacity, ABI_VERSION_V3));
        let owner = Arc::new(Mutex::new(OwnerV3::default()));
        let authoritative_epoch = Arc::new(AtomicU64::new(0));
        let cancel_epoch = Arc::new(AtomicU64::new(0));
        let network_changed_epoch = Arc::new(AtomicU64::new(0));
        let relay_only = Arc::new(AtomicBool::new(true));
        let worker_alive = Arc::new(AtomicBool::new(true));

        let worker_events = Arc::clone(&events);
        let worker_owner = Arc::clone(&owner);
        let worker_cancel = Arc::clone(&cancel_epoch);
        let worker_network_changed = Arc::clone(&network_changed_epoch);
        let worker_relay_only = Arc::clone(&relay_only);
        let worker_alive_flag = Arc::clone(&worker_alive);
        thread::Builder::new()
            .name("termlink-direct-v3-shadow".into())
            .spawn(move || {
                worker_loop(
                    config,
                    command_receiver,
                    signature_receiver,
                    worker_events,
                    worker_owner,
                    worker_cancel,
                    worker_network_changed,
                    worker_relay_only,
                    worker_alive_flag,
                )
            })
            .map_err(|_| ())?;
        Ok(Self {
            config,
            commands,
            signatures,
            events,
            owner,
            authoritative_epoch,
            cancel_epoch,
            network_changed_epoch,
            relay_only,
            worker_alive,
        })
    }

    fn owner(&self) -> OwnerV3 {
        *self.owner.lock().unwrap_or_else(|error| error.into_inner())
    }

    fn is_current(&self, epoch: u64, attempt_id: [u8; 16]) -> bool {
        self.owner().matches(epoch, attempt_id)
    }

    fn publish_stale(&self, epoch: u64, attempt_id: [u8; 16]) {
        let _ = self.events.push_best_effort(stale_event_v3(
            epoch,
            attempt_id,
            self.authoritative_epoch.load(Ordering::Acquire),
        ));
    }

    fn fail_if_current(&self, epoch: u64, attempt_id: [u8; 16], reason: u32) -> bool {
        let owner = self.owner.lock().unwrap_or_else(|error| error.into_inner());
        if !owner.matches(epoch, attempt_id) {
            return false;
        }
        self.cancel_epoch.fetch_max(epoch, Ordering::AcqRel);
        self.relay_only.store(true, Ordering::Release);
        self.events.force_failure(epoch, attempt_id, reason);
        true
    }

    fn accept_gather(&self, gather: TermLinkDirectGatherV3) -> i32 {
        if validate_gather(self.config, &gather).is_err() {
            return -2;
        }
        let mut owner = self.owner.lock().unwrap_or_else(|error| error.into_inner());
        if gather.epoch <= owner.epoch {
            let current = owner.epoch;
            drop(owner);
            let _ = self.events.push_best_effort(stale_event_v3(
                gather.epoch,
                gather.attempt_id,
                current,
            ));
            return 0;
        }
        if owner.epoch != 0 {
            self.cancel_epoch.fetch_max(owner.epoch, Ordering::AcqRel);
        }
        *owner = OwnerV3 {
            epoch: gather.epoch,
            attempt_id: gather.attempt_id,
        };
        self.authoritative_epoch
            .store(gather.epoch, Ordering::Release);
        self.relay_only.store(true, Ordering::Release);
        drop(owner);
        self.submit_command(
            CommandV3::Gather(Box::new(gather)),
            gather.epoch,
            gather.attempt_id,
        )
    }

    fn accept_nominate(&self, nominate: TermLinkDirectNominateV3) -> i32 {
        if nominate.abi_version != ABI_VERSION_V3
            || nominate.struct_size as usize != size_of::<TermLinkDirectNominateV3>()
            || nominate.start_v2.epoch == 0
            || nominate.start_v2.epoch > i64::MAX as u64
            || nominate.start_v2.attempt_id == [0; 16]
        {
            return -2;
        }
        let epoch = nominate.start_v2.epoch;
        let attempt_id = nominate.start_v2.attempt_id;
        let owner = self.owner();
        if owner.epoch == 0 {
            self.events
                .force_failure(epoch, attempt_id, REASON_INVALID_EPOCH_V2);
            return 0;
        }
        if !owner.matches(epoch, attempt_id) {
            self.publish_stale(epoch, attempt_id);
            return 0;
        }
        self.submit_command(CommandV3::Nominate(Box::new(nominate)), epoch, attempt_id)
    }

    fn accept_control(&self, control: TermLinkDirectControlV2, network_change: bool) -> i32 {
        if control.abi_version != ABI_VERSION_V3
            || control.struct_size as usize != size_of::<TermLinkDirectControlV2>()
            || control.epoch == 0
            || control.epoch > i64::MAX as u64
            || control.attempt_id == [0; 16]
        {
            return -2;
        }
        let owner = self.owner.lock().unwrap_or_else(|error| error.into_inner());
        if !owner.matches(control.epoch, control.attempt_id) {
            drop(owner);
            self.publish_stale(control.epoch, control.attempt_id);
            return 0;
        }
        self.cancel_epoch.fetch_max(control.epoch, Ordering::AcqRel);
        let reason = if network_change {
            self.network_changed_epoch
                .fetch_max(control.epoch, Ordering::AcqRel);
            REASON_NETWORK_CHANGED_V2
        } else {
            REASON_CANCELLED_V2
        };
        self.relay_only.store(true, Ordering::Release);
        self.events
            .force_failure(control.epoch, control.attempt_id, reason);
        drop(owner);
        let command = if network_change {
            CommandV3::NetworkChanged(control)
        } else {
            CommandV3::Cancel(control)
        };
        self.submit_command(command, control.epoch, control.attempt_id)
    }

    fn accept_signature(&self, signature: TermLinkDirectSignatureV2) -> i32 {
        if signature.abi_version != ABI_VERSION_V3
            || signature.struct_size as usize != size_of::<TermLinkDirectSignatureV2>()
            || signature.epoch == 0
            || signature.epoch > i64::MAX as u64
            || signature.attempt_id == [0; 16]
            || signature.request_id == 0
            || signature.signature_length == 0
            || signature.signature_length as usize > MAX_SIGNATURE_BYTES_V2
            || signature.reserved != 0
        {
            return -2;
        }
        if !self.is_current(signature.epoch, signature.attempt_id) {
            self.publish_stale(signature.epoch, signature.attempt_id);
            return 0;
        }
        match self.signatures.try_send(Box::new(signature)) {
            Ok(()) => 0,
            Err(TrySendError::Full(_)) => {
                if self.fail_if_current(signature.epoch, signature.attempt_id, REASON_QUEUE_FULL_V2)
                {
                    -4
                } else {
                    self.publish_stale(signature.epoch, signature.attempt_id);
                    0
                }
            }
            Err(TrySendError::Disconnected(_)) => {
                if !self.fail_if_current(signature.epoch, signature.attempt_id, REASON_INTERNAL_V2)
                {
                    self.publish_stale(signature.epoch, signature.attempt_id);
                }
                -1
            }
        }
    }

    fn submit_command(&self, command: CommandV3, epoch: u64, attempt_id: [u8; 16]) -> i32 {
        match self.commands.try_send(command) {
            Ok(()) => 0,
            Err(TrySendError::Full(_)) => {
                if self.fail_if_current(epoch, attempt_id, REASON_QUEUE_FULL_V2) {
                    -4
                } else {
                    self.publish_stale(epoch, attempt_id);
                    0
                }
            }
            Err(TrySendError::Disconnected(_)) => {
                if !self.fail_if_current(epoch, attempt_id, REASON_INTERNAL_V2) {
                    self.publish_stale(epoch, attempt_id);
                }
                -1
            }
        }
    }
}

impl Drop for NativeBridgeV3 {
    fn drop(&mut self) {
        let owner = self.owner();
        if owner.epoch != 0 {
            self.cancel_epoch.fetch_max(owner.epoch, Ordering::AcqRel);
        }
        self.relay_only.store(true, Ordering::Release);
    }
}

#[allow(clippy::too_many_arguments)]
fn worker_loop(
    config: TermLinkDirectCreateV3,
    receiver: Receiver<CommandV3>,
    signatures: Receiver<Box<TermLinkDirectSignatureV2>>,
    events: Arc<crate::v2::EventQueueV2>,
    owner: Arc<Mutex<OwnerV3>>,
    cancel_epoch: Arc<AtomicU64>,
    network_changed_epoch: Arc<AtomicU64>,
    relay_only: Arc<AtomicBool>,
    alive: Arc<AtomicBool>,
) {
    let mut retained: Option<RetainedSocketV3> = None;
    while let Ok(command) = receiver.recv() {
        match command {
            CommandV3::Gather(gather) => {
                if !owner_matches(&owner, gather.epoch, gather.attempt_id) {
                    let _ = events.push_best_effort(stale_event_v3(
                        gather.epoch,
                        gather.attempt_id,
                        current_owner(&owner).epoch,
                    ));
                    continue;
                }
                retained.take();
                match run_gather(
                    config,
                    *gather,
                    &events,
                    &owner,
                    &cancel_epoch,
                    &network_changed_epoch,
                ) {
                    Ok(socket)
                        if owner_matches(&owner, socket.epoch, socket.attempt_id)
                            && !attempt_cancelled(
                                &owner,
                                &cancel_epoch,
                                &network_changed_epoch,
                                socket.epoch,
                                socket.attempt_id,
                            ) =>
                    {
                        retained = Some(socket);
                    }
                    Ok(_) => {}
                    Err(reason) if owner_matches(&owner, gather.epoch, gather.attempt_id) => {
                        relay_only.store(true, Ordering::Release);
                        events.force_failure(gather.epoch, gather.attempt_id, reason);
                    }
                    Err(_) => {}
                }
            }
            CommandV3::Nominate(nominate) => {
                let epoch = nominate.start_v2.epoch;
                let attempt_id = nominate.start_v2.attempt_id;
                if !owner_matches(&owner, epoch, attempt_id) {
                    let _ = events.push_best_effort(stale_event_v3(
                        epoch,
                        attempt_id,
                        current_owner(&owner).epoch,
                    ));
                    continue;
                }
                let Some(current) = retained.as_ref() else {
                    relay_only.store(true, Ordering::Release);
                    events.force_failure(epoch, attempt_id, REASON_INVALID_EPOCH_V2);
                    continue;
                };
                if current.epoch != epoch || current.attempt_id != attempt_id {
                    let _ =
                        events.push_best_effort(stale_event_v3(epoch, attempt_id, current.epoch));
                    continue;
                }
                let validated = match prepare_nomination(config, &nominate, current) {
                    Ok(value) => value,
                    Err(reason) => {
                        retained.take();
                        relay_only.store(true, Ordering::Release);
                        events.force_failure(epoch, attempt_id, reason);
                        continue;
                    }
                };
                let current = retained.take().expect("retained owner checked above");
                let result = crate::v2::run_bound_attempt(
                    nominate.start_v2,
                    nominate.peer_receive_probe_hmac_key,
                    current.identity,
                    current.socket,
                    validated,
                    &signatures,
                    &events,
                    &cancel_epoch,
                    &network_changed_epoch,
                    &relay_only,
                );
                if let Err(reason) = result
                    && owner_matches(&owner, epoch, attempt_id)
                {
                    relay_only.store(true, Ordering::Release);
                    events.force_failure(epoch, attempt_id, reason);
                }
            }
            CommandV3::Cancel(control) | CommandV3::NetworkChanged(control) => {
                if retained.as_ref().is_some_and(|socket| {
                    socket.epoch == control.epoch && socket.attempt_id == control.attempt_id
                }) {
                    retained.take();
                }
            }
        }
    }
    retained.take();
    alive.store(false, Ordering::Release);
}

impl crate::v2::SignatureInbox for Receiver<Box<TermLinkDirectSignatureV2>> {
    fn wait_signature(
        &self,
        events: &crate::v2::EventQueueV2,
        cancel_epoch: &AtomicU64,
        network_changed_epoch: &AtomicU64,
        epoch: u64,
        attempt_id: [u8; 16],
        request_id: u64,
        timeout: Duration,
    ) -> Result<Vec<u8>, u32> {
        let deadline = Instant::now() + timeout;
        loop {
            if cancelled_through(cancel_epoch, epoch)
                || cancelled_through(network_changed_epoch, epoch)
            {
                return Err(cancellation_reason_v3(
                    cancel_epoch,
                    network_changed_epoch,
                    epoch,
                    REASON_CANCELLED_V2,
                ));
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(REASON_SIGNATURE_TIMEOUT_V2);
            }
            match self.recv_timeout(remaining.min(Duration::from_millis(50))) {
                Ok(signature)
                    if signature.abi_version == ABI_VERSION_V3
                        && signature.struct_size as usize
                            == size_of::<TermLinkDirectSignatureV2>()
                        && signature.epoch == epoch
                        && signature.attempt_id == attempt_id
                        && signature.request_id == request_id
                        && signature.signature_length > 0
                        && signature.signature_length as usize <= MAX_SIGNATURE_BYTES_V2
                        && signature.reserved == 0 =>
                {
                    return Ok(signature.signature[..signature.signature_length as usize].to_vec());
                }
                Ok(signature) => {
                    let _ = events.push_best_effort(stale_event_v3(
                        signature.epoch,
                        signature.attempt_id,
                        epoch,
                    ));
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => return Err(REASON_CANCELLED_V2),
            }
        }
    }
}

fn current_owner(owner: &Mutex<OwnerV3>) -> OwnerV3 {
    *owner.lock().unwrap_or_else(|error| error.into_inner())
}

fn owner_matches(owner: &Mutex<OwnerV3>, epoch: u64, attempt_id: [u8; 16]) -> bool {
    current_owner(owner).matches(epoch, attempt_id)
}

fn cancelled_through(value: &AtomicU64, epoch: u64) -> bool {
    let value = value.load(Ordering::Acquire);
    value != 0 && value >= epoch
}

fn attempt_cancelled(
    owner: &Mutex<OwnerV3>,
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    epoch: u64,
    attempt_id: [u8; 16],
) -> bool {
    !owner_matches(owner, epoch, attempt_id)
        || cancelled_through(cancel_epoch, epoch)
        || cancelled_through(network_changed_epoch, epoch)
}

fn cancellation_reason_v3(
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
    epoch: u64,
    fallback: u32,
) -> u32 {
    if cancelled_through(network_changed_epoch, epoch) {
        REASON_NETWORK_CHANGED_V2
    } else if cancelled_through(cancel_epoch, epoch) {
        REASON_CANCELLED_V2
    } else {
        fallback
    }
}

fn validate_gather(
    config: TermLinkDirectCreateV3,
    gather: &TermLinkDirectGatherV3,
) -> Result<(), u32> {
    if gather.abi_version != ABI_VERSION_V3
        || gather.struct_size as usize != size_of::<TermLinkDirectGatherV3>()
        || gather.epoch == 0
        || gather.epoch > i64::MAX as u64
        || gather.attempt_id == [0; 16]
        || gather.reserved0 != 0
        || gather.reserved != [0; 4]
    {
        return Err(REASON_INVALID_ABI_V2);
    }
    if (config.platform == PLATFORM_ANDROID_V2 && gather.network_handle == 0)
        || (config.platform == PLATFORM_WINDOWS_V2 && gather.network_handle != 0)
    {
        return Err(REASON_INVALID_PLATFORM_V2);
    }
    let bind = decode_socket_v3(gather.local_bind, true)?;
    let advertised = decode_ip(
        gather.advertised_local_ip,
        gather.advertised_local_ip_length,
    )?;
    if bind.is_ipv4() != advertised.is_ipv4() {
        return Err(REASON_INVALID_CANDIDATE_V2);
    }
    let stun_count = gather.stun_count as usize;
    if stun_count > MAX_STUN_SERVERS || !(100..=5_000).contains(&gather.gather_timeout_ms) {
        return Err(REASON_INVALID_ABI_V2);
    }
    for value in &gather.stun_servers[..stun_count] {
        decode_socket_v3(*value, false)?;
    }
    if gather.stun_servers[stun_count..]
        .iter()
        .any(|value| *value != TermLinkSocketAddrV2::default())
    {
        return Err(REASON_INVALID_ABI_V2);
    }
    Ok(())
}

fn run_gather(
    config: TermLinkDirectCreateV3,
    gather: TermLinkDirectGatherV3,
    events: &crate::v2::EventQueueV2,
    owner: &Mutex<OwnerV3>,
    cancel_epoch: &AtomicU64,
    network_changed_epoch: &AtomicU64,
) -> Result<RetainedSocketV3, u32> {
    validate_gather(config, &gather)?;
    if config.enabled == 0 {
        return Err(REASON_DISABLED_V2);
    }
    let identity = TemporaryIdentity::generate().map_err(|_| REASON_INTERNAL_V2)?;
    let certificate = identity.certificate_der();
    if certificate.len() > MAX_IDENTITY_CERTIFICATE_BYTES_V2 {
        return Err(REASON_INTERNAL_V2);
    }
    let mut identity_event = TermLinkDirectEventV2 {
        kind: EVENT_IDENTITY_READY_V2,
        state: STATE_GATHERING_V2,
        epoch: gather.epoch,
        attempt_id: gather.attempt_id,
        payload_length: certificate.len() as u32,
        ..TermLinkDirectEventV2::default()
    };
    identity_event.payload[..certificate.len()].copy_from_slice(certificate.as_ref());
    if !events.push(identity_event) {
        cancel_epoch.fetch_max(gather.epoch, Ordering::AcqRel);
        return Err(REASON_QUEUE_FULL_V2);
    }
    let bind = decode_socket_v3(gather.local_bind, true)?;
    let advertised_ip = decode_ip(
        gather.advertised_local_ip,
        gather.advertised_local_ip_length,
    )?;
    let stun_servers = gather.stun_servers[..gather.stun_count as usize]
        .iter()
        .map(|value| decode_socket_v3(*value, false))
        .collect::<Result<Vec<_>, _>>()?;
    let socket = bind_socket_v3(config.platform, bind, gather.network_handle)?;
    let host = socket
        .host_candidate(advertised_ip)
        .map_err(|_| REASON_INVALID_CANDIDATE_V2)?
        .endpoint;
    let srflx = socket
        .gather_server_reflexive(
            &stun_servers,
            Duration::from_millis(u64::from(gather.gather_timeout_ms)),
            || {
                attempt_cancelled(
                    owner,
                    cancel_epoch,
                    network_changed_epoch,
                    gather.epoch,
                    gather.attempt_id,
                )
            },
        )
        .map_err(|_| {
            cancellation_reason_v3(
                cancel_epoch,
                network_changed_epoch,
                gather.epoch,
                REASON_SOCKET_V2,
            )
        })?;
    if attempt_cancelled(
        owner,
        cancel_epoch,
        network_changed_epoch,
        gather.epoch,
        gather.attempt_id,
    ) {
        return Err(cancellation_reason_v3(
            cancel_epoch,
            network_changed_epoch,
            gather.epoch,
            REASON_CANCELLED_V2,
        ));
    }
    let mut candidates = TermLinkDirectCandidatesV3::default();
    candidates.candidates[0] = encode_socket_v3(host);
    candidates.candidate_count = 1;
    for candidate in srflx.into_iter().take(MAX_CANDIDATES_V3 - 1) {
        if candidate != host {
            let index = candidates.candidate_count as usize;
            candidates.candidates[index] = encode_socket_v3(candidate);
            candidates.candidate_count += 1;
        }
    }
    let bytes = unsafe {
        std::slice::from_raw_parts(
            (&raw const candidates).cast::<u8>(),
            size_of::<TermLinkDirectCandidatesV3>(),
        )
    };
    let mut event = TermLinkDirectEventV2 {
        kind: EVENT_CANDIDATES_READY_V3,
        state: STATE_GATHERING_V2,
        epoch: gather.epoch,
        attempt_id: gather.attempt_id,
        endpoint: encode_socket_v3(socket.bound_addr()),
        payload_length: bytes.len() as u32,
        ..TermLinkDirectEventV2::default()
    };
    event.payload[..bytes.len()].copy_from_slice(bytes);
    if !events.push(event) {
        cancel_epoch.fetch_max(gather.epoch, Ordering::AcqRel);
        return Err(REASON_QUEUE_FULL_V2);
    }
    Ok(RetainedSocketV3 {
        epoch: gather.epoch,
        attempt_id: gather.attempt_id,
        network_handle: gather.network_handle,
        identity,
        socket,
    })
}

fn prepare_nomination(
    config: TermLinkDirectCreateV3,
    nominate: &TermLinkDirectNominateV3,
    retained: &RetainedSocketV3,
) -> Result<crate::v2::ValidatedStart, u32> {
    if nominate.abi_version != ABI_VERSION_V3
        || nominate.struct_size as usize != size_of::<TermLinkDirectNominateV3>()
        || nominate.start_v2.abi_version != ABI_VERSION_V2
        || nominate.start_v2.struct_size as usize != size_of::<TermLinkDirectStartV2>()
        || nominate.start_v2.reserved0 != 0
        || nominate.start_v2.reserved1 != 0
        || nominate.start_v2.reserved != [0; 4]
        || nominate.reserved != [0; 4]
        || nominate.local_send_probe_hmac_key == [0; 32]
        || nominate.peer_receive_probe_hmac_key == [0; 32]
        || nominate.start_v2.probe_hmac_key != nominate.local_send_probe_hmac_key
        || nominate.start_v2.peer_proof_nonce_length != 0
        || nominate.start_v2.peer_proof_nonce != [0; 32]
        || nominate.start_v2.stun_count != 0
        || nominate
            .start_v2
            .stun_servers
            .iter()
            .any(|value| *value != TermLinkSocketAddrV2::default())
    {
        return Err(REASON_INVALID_ABI_V2);
    }
    let mut validated = crate::v2::validate_start(config.platform, &nominate.start_v2)?;
    if validated.local_bind != retained.socket.bound_addr()
        || nominate.start_v2.network_handle != retained.network_handle
        || nominate.start_v2.epoch != retained.epoch
        || nominate.start_v2.attempt_id != retained.attempt_id
    {
        return Err(REASON_INVALID_EPOCH_V2);
    }
    validated.stun_servers.clear();
    Ok(validated)
}

fn decode_ip(bytes: [u8; 16], length: u32) -> Result<IpAddr, u32> {
    let ip = match length {
        4 if bytes[4..] == [0; 12] => {
            IpAddr::V4(Ipv4Addr::new(bytes[0], bytes[1], bytes[2], bytes[3]))
        }
        16 => IpAddr::V6(Ipv6Addr::from(bytes)),
        _ => return Err(REASON_INVALID_CANDIDATE_V2),
    };
    validate_candidate_endpoint(SocketAddr::new(ip, 49_152))
        .map_err(|_| REASON_INVALID_CANDIDATE_V2)?;
    Ok(ip)
}

fn decode_socket_v3(value: TermLinkSocketAddrV2, allow_zero_port: bool) -> Result<SocketAddr, u32> {
    let ip = decode_ip(value.ip, value.ip_length)?;
    let port = u16::try_from(value.port).map_err(|_| REASON_INVALID_CANDIDATE_V2)?;
    if (!allow_zero_port && port < 1024) || (allow_zero_port && port != 0 && port < 1024) {
        return Err(REASON_INVALID_CANDIDATE_V2);
    }
    Ok(SocketAddr::new(ip, port))
}

fn encode_socket_v3(value: SocketAddr) -> TermLinkSocketAddrV2 {
    let mut encoded = TermLinkSocketAddrV2 {
        port: u32::from(value.port()),
        ..TermLinkSocketAddrV2::default()
    };
    match value.ip() {
        IpAddr::V4(ip) => {
            encoded.ip[..4].copy_from_slice(&ip.octets());
            encoded.ip_length = 4;
        }
        IpAddr::V6(ip) => {
            encoded.ip.copy_from_slice(&ip.octets());
            encoded.ip_length = 16;
        }
    }
    encoded
}

fn bind_socket_v3(
    platform: u32,
    address: SocketAddr,
    network_handle: u64,
) -> Result<DirectUdpSocket, u32> {
    match platform {
        PLATFORM_WINDOWS_V2 => DirectUdpSocket::bind(address).map_err(|_| REASON_SOCKET_V2),
        PLATFORM_ANDROID_V2 => {
            #[cfg(target_os = "android")]
            {
                let binder = AndroidNetworkBinder::new(network_handle)
                    .map_err(|_| REASON_INVALID_PLATFORM_V2)?;
                DirectUdpSocket::bind_with_platform(address, &binder).map_err(|_| REASON_SOCKET_V2)
            }
            #[cfg(not(target_os = "android"))]
            {
                let _ = (address, network_handle);
                Err(REASON_INVALID_PLATFORM_V2)
            }
        }
        _ => Err(REASON_INVALID_PLATFORM_V2),
    }
}

fn stale_event_v3(
    received_epoch: u64,
    attempt_id: [u8; 16],
    current_epoch: u64,
) -> TermLinkDirectEventV2 {
    TermLinkDirectEventV2 {
        kind: EVENT_IGNORED_STALE_V2,
        state: STATE_RELAY_ONLY_V2,
        epoch: current_epoch,
        request_id: received_epoch,
        attempt_id,
        ..TermLinkDirectEventV2::default()
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn termlink_direct_abi_version_v3() -> u32 {
    ABI_VERSION_V3
}

#[unsafe(no_mangle)]
/// # Safety
/// `config` must be readable for one V3 create record for this call only.
pub unsafe extern "C" fn termlink_direct_create_v3(
    config: *const TermLinkDirectCreateV3,
) -> *mut c_void {
    catch_unwind(AssertUnwindSafe(|| {
        if config.is_null() {
            return std::ptr::null_mut();
        }
        let config = unsafe { config.read_unaligned() };
        NativeBridgeV3::create(config)
            .map(|bridge| Box::into_raw(Box::new(bridge)).cast())
            .unwrap_or(std::ptr::null_mut())
    }))
    .unwrap_or(std::ptr::null_mut())
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be live and `input` readable for this call.
pub unsafe extern "C" fn termlink_direct_gather_v3(
    handle: *mut c_void,
    input: *const TermLinkDirectGatherV3,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(bridge) = (unsafe { bridge_v3_ref(handle) }) else {
            return -1;
        };
        if input.is_null() {
            return -2;
        }
        bridge.accept_gather(unsafe { input.read_unaligned() })
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be live and `input` readable for this call.
pub unsafe extern "C" fn termlink_direct_nominate_v3(
    handle: *mut c_void,
    input: *const TermLinkDirectNominateV3,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(bridge) = (unsafe { bridge_v3_ref(handle) }) else {
            return -1;
        };
        if input.is_null() {
            return -2;
        }
        bridge.accept_nominate(unsafe { input.read_unaligned() })
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be live and `control` readable for this call.
pub unsafe extern "C" fn termlink_direct_cancel_v3(
    handle: *mut c_void,
    control: *const TermLinkDirectControlV2,
) -> i32 {
    unsafe { submit_control_v3(handle, control, false) }
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be live and `control` readable for this call.
pub unsafe extern "C" fn termlink_direct_network_changed_v3(
    handle: *mut c_void,
    control: *const TermLinkDirectControlV2,
) -> i32 {
    unsafe { submit_control_v3(handle, control, true) }
}

unsafe fn submit_control_v3(
    handle: *mut c_void,
    control: *const TermLinkDirectControlV2,
    network_change: bool,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(bridge) = (unsafe { bridge_v3_ref(handle) }) else {
            return -1;
        };
        if control.is_null() {
            return -2;
        }
        bridge.accept_control(unsafe { control.read_unaligned() }, network_change)
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be live and `signature` readable for this call.
pub unsafe extern "C" fn termlink_direct_submit_signature_v3(
    handle: *mut c_void,
    signature: *const TermLinkDirectSignatureV2,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(bridge) = (unsafe { bridge_v3_ref(handle) }) else {
            return -1;
        };
        if signature.is_null() {
            return -2;
        }
        bridge.accept_signature(unsafe { signature.read_unaligned() })
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be live and `output` writable for one V2-layout event.
pub unsafe extern "C" fn termlink_direct_poll_event_v3(
    handle: *mut c_void,
    output: *mut TermLinkDirectEventV2,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(bridge) = (unsafe { bridge_v3_ref(handle) }) else {
            return -1;
        };
        if output.is_null() {
            return -2;
        }
        let Some(event) = bridge.events.poll() else {
            return 0;
        };
        unsafe { output.write_unaligned(event) };
        1
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be live and not reused after this call.
pub unsafe extern "C" fn termlink_direct_destroy_v3(handle: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !handle.is_null() {
            let bridge = unsafe { Box::from_raw(handle.cast::<NativeBridgeV3>()) };
            drop(bridge);
        }
    }));
}

unsafe fn bridge_v3_ref<'a>(handle: *mut c_void) -> Option<&'a NativeBridgeV3> {
    if handle.is_null() {
        None
    } else {
        Some(unsafe { &*handle.cast::<NativeBridgeV3>() })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        EVENT_DIRECT_FAILED_V2, EVENT_SHADOW_READY_V2, EVENT_SIGN_REQUEST_V2, ROLE_INITIATOR_V2,
        ROLE_RESPONDER_V2,
    };
    use ring::{
        rand::SystemRandom,
        signature::{Ed25519KeyPair, KeyPair},
    };
    use std::{net::UdpSocket, time::Instant};

    fn create() -> NativeBridgeV3 {
        create_with_capacity(8)
    }

    fn create_with_capacity(queue_capacity: u32) -> NativeBridgeV3 {
        NativeBridgeV3::create(TermLinkDirectCreateV3 {
            abi_version: ABI_VERSION_V3,
            struct_size: size_of::<TermLinkDirectCreateV3>() as u32,
            enabled: 1,
            queue_capacity,
            platform: PLATFORM_WINDOWS_V2,
            reserved: [0; 3],
        })
        .unwrap()
    }

    #[test]
    fn abi_v3_is_distinct_and_fixed() {
        assert_eq!(ABI_VERSION_V2, 2);
        assert_eq!(ABI_VERSION_V3, 3);
        assert_eq!(size_of::<TermLinkDirectCreateV3>(), 32);
        assert_eq!(size_of::<TermLinkDirectGatherV3>(), 176);
        assert_eq!(size_of::<TermLinkDirectNominateV3>(), 2_696);
        assert_eq!(size_of::<TermLinkDirectCandidatesV3>(), 80);
        assert_eq!(std::mem::offset_of!(TermLinkDirectGatherV3, local_bind), 40);
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectGatherV3, stun_servers),
            88
        );
        assert_eq!(std::mem::offset_of!(TermLinkDirectGatherV3, reserved), 144);
        assert_eq!(std::mem::offset_of!(TermLinkDirectNominateV3, start_v2), 8);
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectNominateV3, local_send_probe_hmac_key),
            2_600
        );
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectNominateV3, peer_receive_probe_hmac_key),
            2_632
        );
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectNominateV3, reserved),
            2_664
        );
        assert_eq!(
            std::mem::offset_of!(TermLinkDirectCandidatesV3, candidates),
            8
        );
        let mut noncanonical_ipv4 = [0_u8; 16];
        noncanonical_ipv4[..4].copy_from_slice(&[10, 0, 0, 1]);
        noncanonical_ipv4[4] = 1;
        assert_eq!(
            decode_ip(noncanonical_ipv4, 4),
            Err(REASON_INVALID_CANDIDATE_V2)
        );
    }

    #[test]
    fn gather_emits_identity_and_retains_exact_advertised_socket() {
        let bridge = create();
        let gather = gather(active_ip(), 7, [7; 16]);
        assert_eq!(bridge.accept_gather(gather), 0);
        let identity = await_kind(&bridge, EVENT_IDENTITY_READY_V2);
        assert_eq!((identity.epoch, identity.attempt_id), (7, [7; 16]));
        assert!(identity.payload_length > 0);
        let event = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        assert_eq!(event.endpoint, payload_candidates(&event).candidates[0]);
        assert_eq!(bridge.accept_control(control(7, [7; 16]), false), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_CANCELLED_V2
        );
    }

    #[test]
    fn gather_reports_host_and_server_reflexive_from_one_socket_owner() {
        let ip = active_ip();
        let stun = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        stun.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
        let stun_address = stun.local_addr().unwrap();
        let mapped: SocketAddr = "8.8.8.8:54321".parse().unwrap();
        let responder = thread::spawn(move || {
            let mut request = [0_u8; 64];
            let (length, peer) = stun.recv_from(&mut request).unwrap();
            assert_eq!(length, 20);
            let cookie = termlink_direct_nat::STUN_MAGIC_COOKIE.to_be_bytes();
            let mut response = Vec::from([0x01, 0x01, 0, 12]);
            response.extend_from_slice(&cookie);
            response.extend_from_slice(&request[8..20]);
            response.extend_from_slice(&0x0020_u16.to_be_bytes());
            response.extend_from_slice(&8_u16.to_be_bytes());
            response.extend_from_slice(&[0, 1]);
            response.extend_from_slice(
                &(mapped.port() ^ (termlink_direct_nat::STUN_MAGIC_COOKIE >> 16) as u16)
                    .to_be_bytes(),
            );
            let mapped_ip = match mapped.ip() {
                IpAddr::V4(value) => value.octets(),
                IpAddr::V6(_) => unreachable!(),
            };
            for index in 0..4 {
                response.push(mapped_ip[index] ^ cookie[index]);
            }
            stun.send_to(&response, peer).unwrap();
        });
        let bridge = create();
        let mut input = gather(ip, 9, [9; 16]);
        input.stun_count = 1;
        input.stun_servers[0] = encode_socket_v3(stun_address);
        input.gather_timeout_ms = 500;
        assert_eq!(bridge.accept_gather(input), 0);
        let event = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        let candidates = payload_candidates(&event);
        assert_eq!(candidates.candidate_count, 2);
        assert_eq!(
            decode_socket_v3(candidates.candidates[1], false),
            Ok(mapped)
        );
        assert_eq!(event.endpoint, candidates.candidates[0]);
        responder.join().unwrap();
    }

    #[test]
    fn invalid_or_stale_gather_cannot_retire_new_owner() {
        let bridge = create();
        let ip = active_ip();
        assert_eq!(bridge.accept_gather(gather(ip, 8, [8; 16])), 0);
        let _ = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        let mut malformed = gather(ip, 9, [9; 16]);
        malformed.reserved[0] = 1;
        assert_eq!(bridge.accept_gather(malformed), -2);
        assert_eq!(bridge.authoritative_epoch.load(Ordering::Acquire), 8);
        assert_eq!(bridge.accept_gather(gather(ip, 7, [7; 16])), 0);
        assert_eq!(await_kind(&bridge, EVENT_IGNORED_STALE_V2).epoch, 8);
        assert_eq!(bridge.accept_control(control(8, [8; 16]), false), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_CANCELLED_V2
        );
    }

    #[test]
    fn cancel_interrupts_blocked_gather_immediately() {
        let bridge = create();
        let ip = active_ip();
        let blackhole = UdpSocket::bind(SocketAddr::new(ip, 0)).unwrap();
        let mut input = gather(ip, 11, [11; 16]);
        input.gather_timeout_ms = 5_000;
        input.stun_count = 1;
        input.stun_servers[0] = encode_socket_v3(blackhole.local_addr().unwrap());
        assert_eq!(bridge.accept_gather(input), 0);
        let _ = await_kind(&bridge, EVENT_IDENTITY_READY_V2);
        let started = Instant::now();
        assert_eq!(bridge.accept_control(control(11, [11; 16]), false), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_CANCELLED_V2
        );
        assert_eq!(bridge.accept_gather(gather(ip, 12, [12; 16])), 0);
        let _ = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        assert!(started.elapsed() < Duration::from_millis(500));
    }

    #[test]
    fn stale_controls_never_touch_current_owner() {
        let bridge = create();
        assert_eq!(bridge.accept_gather(gather(active_ip(), 8, [8; 16])), 0);
        let _ = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        assert_eq!(bridge.accept_control(control(7, [7; 16]), false), 0);
        assert_eq!(bridge.accept_control(control(7, [7; 16]), true), 0);
        assert_eq!(bridge.cancel_epoch.load(Ordering::Acquire), 0);
        assert_eq!(bridge.network_changed_epoch.load(Ordering::Acquire), 0);
        assert_eq!(bridge.accept_control(control(8, [8; 16]), true), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_NETWORK_CHANGED_V2
        );
    }

    #[test]
    fn stale_nomination_preserves_new_socket() {
        let bridge = create();
        let stale = nomination_shell(6, [6; 16]);
        assert_eq!(bridge.accept_nominate(stale), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_INVALID_EPOCH_V2
        );
        assert_eq!(bridge.accept_gather(gather(active_ip(), 8, [8; 16])), 0);
        let ready = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        assert_eq!(bridge.accept_nominate(stale), 0);
        assert_eq!(await_kind(&bridge, EVENT_IGNORED_STALE_V2).epoch, 8);
        assert_eq!(bridge.accept_control(control(8, [8; 16]), false), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_CANCELLED_V2
        );
        assert_eq!(ready.endpoint, payload_candidates(&ready).candidates[0]);
    }

    #[test]
    fn malformed_current_nomination_retires_only_optional_attempt() {
        let bridge = create();
        assert_eq!(bridge.accept_gather(gather(active_ip(), 12, [12; 16])), 0);
        let _ = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        assert_eq!(bridge.accept_nominate(nomination_shell(12, [12; 16])), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_INVALID_ABI_V2
        );
        assert!(bridge.relay_only.load(Ordering::Acquire));
    }

    #[test]
    fn gather_then_nominate_completes_mutual_shadow_without_rebinding() {
        let ip = active_ip();
        let initiator = create_with_capacity(32);
        let responder = create_with_capacity(32);
        let attempt_id = [0x44; 16];
        assert_eq!(initiator.accept_gather(gather(ip, 7, attempt_id)), 0);
        assert_eq!(responder.accept_gather(gather(ip, 7, attempt_id)), 0);
        let initiator_certificate = await_kind(&initiator, EVENT_IDENTITY_READY_V2);
        let responder_certificate = await_kind(&responder, EVENT_IDENTITY_READY_V2);
        let initiator_candidates = await_kind(&initiator, EVENT_CANDIDATES_READY_V3);
        let responder_candidates = await_kind(&responder, EVENT_CANDIDATES_READY_V3);
        let initiator_endpoint = decode_socket_v3(initiator_candidates.endpoint, false).unwrap();
        let responder_endpoint = decode_socket_v3(responder_candidates.endpoint, false).unwrap();

        let random = SystemRandom::new();
        let initiator_key =
            Ed25519KeyPair::from_pkcs8(Ed25519KeyPair::generate_pkcs8(&random).unwrap().as_ref())
                .unwrap();
        let responder_key =
            Ed25519KeyPair::from_pkcs8(Ed25519KeyPair::generate_pkcs8(&random).unwrap().as_ref())
                .unwrap();
        let initiator_public: [u8; 32] = initiator_key.public_key().as_ref().try_into().unwrap();
        let responder_public: [u8; 32] = responder_key.public_key().as_ref().try_into().unwrap();
        let ticket = base64url(
            "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI",
        );
        let server_key: [u8; 32] = base64url("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg")
            .try_into()
            .unwrap();
        let initiator_device = hex("20314253647548698a9bacbdcedff001");
        let responder_device = hex("304152637485496a8b9cadbecfe0f102");
        let connection_id = hex("4051627384954a6b8c9daebfd0e1f203");
        let mut initiator_start = nomination_start(
            initiator_endpoint,
            responder_endpoint,
            ROLE_INITIATOR_V2,
            attempt_id,
            connection_id,
            initiator_device,
            responder_device,
            &ticket,
            server_key,
        );
        let mut responder_start = nomination_start(
            responder_endpoint,
            initiator_endpoint,
            ROLE_RESPONDER_V2,
            attempt_id,
            connection_id,
            responder_device,
            initiator_device,
            &ticket,
            server_key,
        );
        initiator_start.peer_certificate_sha256 =
            termlink_direct_wire::sha256(&initiator_event_bytes(&responder_certificate));
        responder_start.peer_certificate_sha256 =
            termlink_direct_wire::sha256(&initiator_event_bytes(&initiator_certificate));
        initiator_start.peer_device_signing_public_key = responder_public;
        responder_start.peer_device_signing_public_key = initiator_public;
        initiator_start.local_probe_nonce = [0xa1; 16];
        initiator_start.peer_probe_nonce = [0xb2; 16];
        responder_start.local_probe_nonce = initiator_start.peer_probe_nonce;
        responder_start.peer_probe_nonce = initiator_start.local_probe_nonce;
        initiator_start.local_proof_nonce = [0xc3; 32];
        responder_start.local_proof_nonce = [0xd4; 32];
        responder_start.probe_hmac_key = [0x43; 32];

        let mut initiator_nomination = nomination(initiator_start);
        initiator_nomination.peer_receive_probe_hmac_key = [0x43; 32];
        let mut responder_nomination = nomination(responder_start);
        responder_nomination.peer_receive_probe_hmac_key = [0x42; 32];
        assert_eq!(initiator.accept_nominate(initiator_nomination), 0);
        assert_eq!(responder.accept_nominate(responder_nomination), 0);

        let deadline = Instant::now() + Duration::from_secs(8);
        let mut initiator_binding = None;
        let mut responder_binding = None;
        while (initiator_binding.is_none() || responder_binding.is_none())
            && Instant::now() < deadline
        {
            service_events(
                &initiator,
                &initiator_key,
                &mut initiator_binding,
                "initiator",
            );
            service_events(
                &responder,
                &responder_key,
                &mut responder_binding,
                "responder",
            );
            thread::yield_now();
        }
        let initiator_binding = initiator_binding.expect("initiator V3 shadow not ready");
        let responder_binding = responder_binding.expect("responder V3 shadow not ready");
        assert_eq!(initiator_binding, responder_binding);
        assert!(!initiator.relay_only.load(Ordering::Acquire));
        assert!(!responder.relay_only.load(Ordering::Acquire));
    }

    #[test]
    fn stale_signature_pressure_cannot_cancel_current_owner() {
        // Gather publishes IdentityReady followed by CandidatesReady. A capacity of one makes
        // this test depend on the test thread polling between those two worker publications;
        // under parallel test load the second authoritative event correctly overflows before
        // the stale-signature behavior being tested is reached.
        let bridge = create_with_capacity(2);
        assert_eq!(bridge.accept_gather(gather(active_ip(), 8, [8; 16])), 0);
        let _ = await_kind(&bridge, EVENT_CANDIDATES_READY_V3);
        for request_id in 1..=32 {
            assert_eq!(
                bridge.accept_signature(signature(7, [7; 16], request_id)),
                0
            );
        }
        assert_eq!(bridge.cancel_epoch.load(Ordering::Acquire), 0);
        assert_eq!(bridge.accept_control(control(8, [8; 16]), false), 0);
        assert_eq!(
            await_kind(&bridge, EVENT_DIRECT_FAILED_V2).reason,
            REASON_CANCELLED_V2
        );
    }

    #[test]
    fn repeated_gather_destroy_workers_exit_bounded() {
        let mut alive = Vec::new();
        for epoch in 1..=16 {
            let bridge = create();
            assert_eq!(
                bridge.accept_gather(gather(active_ip(), epoch, [epoch as u8; 16])),
                0
            );
            alive.push(Arc::clone(&bridge.worker_alive));
            drop(bridge);
        }
        let deadline = Instant::now() + Duration::from_secs(2);
        while alive.iter().any(|value| value.load(Ordering::Acquire)) {
            assert!(Instant::now() < deadline, "detached V3 workers leaked");
            thread::yield_now();
        }
    }

    fn gather(ip: IpAddr, epoch: u64, attempt_id: [u8; 16]) -> TermLinkDirectGatherV3 {
        let encoded = encode_socket_v3(SocketAddr::new(ip, 0));
        TermLinkDirectGatherV3 {
            abi_version: ABI_VERSION_V3,
            struct_size: size_of::<TermLinkDirectGatherV3>() as u32,
            epoch,
            attempt_id,
            local_bind: encoded,
            advertised_local_ip: encoded.ip,
            advertised_local_ip_length: encoded.ip_length,
            gather_timeout_ms: 100,
            ..TermLinkDirectGatherV3::default()
        }
    }

    fn control(epoch: u64, attempt_id: [u8; 16]) -> TermLinkDirectControlV2 {
        TermLinkDirectControlV2 {
            abi_version: ABI_VERSION_V3,
            struct_size: size_of::<TermLinkDirectControlV2>() as u32,
            epoch,
            attempt_id,
        }
    }

    fn signature(epoch: u64, attempt_id: [u8; 16], request_id: u64) -> TermLinkDirectSignatureV2 {
        TermLinkDirectSignatureV2 {
            abi_version: ABI_VERSION_V3,
            struct_size: size_of::<TermLinkDirectSignatureV2>() as u32,
            epoch,
            attempt_id,
            request_id,
            signature_length: 64,
            signature: [1; MAX_SIGNATURE_BYTES_V2],
            ..TermLinkDirectSignatureV2::default()
        }
    }

    fn nomination_shell(epoch: u64, attempt_id: [u8; 16]) -> TermLinkDirectNominateV3 {
        TermLinkDirectNominateV3 {
            abi_version: ABI_VERSION_V3,
            struct_size: size_of::<TermLinkDirectNominateV3>() as u32,
            start_v2: TermLinkDirectStartV2 {
                epoch,
                attempt_id,
                ..TermLinkDirectStartV2::default()
            },
            local_send_probe_hmac_key: [0x42; 32],
            peer_receive_probe_hmac_key: [0x42; 32],
            reserved: [0; 4],
        }
    }

    fn nomination(start_v2: TermLinkDirectStartV2) -> TermLinkDirectNominateV3 {
        let local_send_probe_hmac_key = start_v2.probe_hmac_key;
        TermLinkDirectNominateV3 {
            abi_version: ABI_VERSION_V3,
            struct_size: size_of::<TermLinkDirectNominateV3>() as u32,
            start_v2,
            local_send_probe_hmac_key,
            peer_receive_probe_hmac_key: local_send_probe_hmac_key,
            reserved: [0; 4],
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn nomination_start(
        local: SocketAddr,
        remote: SocketAddr,
        role: u32,
        attempt_id: [u8; 16],
        connection_id: [u8; 16],
        local_device_id: [u8; 16],
        peer_device_id: [u8; 16],
        ticket: &[u8],
        server_key: [u8; 32],
    ) -> TermLinkDirectStartV2 {
        let mut start = TermLinkDirectStartV2 {
            abi_version: ABI_VERSION_V2,
            struct_size: size_of::<TermLinkDirectStartV2>() as u32,
            epoch: 7,
            attempt_id,
            connection_id,
            local_device_id,
            peer_device_id,
            role,
            timeout_ms: 5_000,
            local_bind: encode_socket_v3(local),
            remote_candidate: encode_socket_v3(remote),
            ticket_length: ticket.len() as u32,
            server_signing_public_key: server_key,
            probe_hmac_key: [0x42; 32],
            ticket_id: hex("00112233445546778899aabbccddeeff"),
            nomination_id: [0x50; 16],
            initiator_candidate_id: [0x60; 16],
            responder_candidate_id: [0x70; 16],
            local_proof_nonce_length: 16,
            peer_proof_nonce_length: 0,
            now_ms: 1_900_000_060_000,
            ..TermLinkDirectStartV2::default()
        };
        start.ticket[..ticket.len()].copy_from_slice(ticket);
        start
    }

    fn service_events(
        bridge: &NativeBridgeV3,
        key: &Ed25519KeyPair,
        ready: &mut Option<[u8; 32]>,
        side: &str,
    ) {
        while let Some(event) = bridge.events.poll() {
            match event.kind {
                EVENT_SIGN_REQUEST_V2 => {
                    let signed = key.sign(&event.payload[..event.payload_length as usize]);
                    let mut response = TermLinkDirectSignatureV2 {
                        abi_version: ABI_VERSION_V3,
                        struct_size: size_of::<TermLinkDirectSignatureV2>() as u32,
                        epoch: event.epoch,
                        attempt_id: event.attempt_id,
                        request_id: event.request_id,
                        signature_length: signed.as_ref().len() as u32,
                        ..TermLinkDirectSignatureV2::default()
                    };
                    response.signature[..signed.as_ref().len()].copy_from_slice(signed.as_ref());
                    assert_eq!(bridge.accept_signature(response), 0);
                }
                EVENT_SHADOW_READY_V2 => {
                    assert_eq!(event.payload_length, 32);
                    *ready = Some(event.payload[..32].try_into().unwrap());
                }
                EVENT_DIRECT_FAILED_V2 => {
                    panic!("{side} V3 shadow failed with reason {}", event.reason)
                }
                _ => {}
            }
        }
    }

    fn initiator_event_bytes(event: &TermLinkDirectEventV2) -> Vec<u8> {
        event.payload[..event.payload_length as usize].to_vec()
    }

    fn hex<const N: usize>(value: &str) -> [u8; N] {
        assert_eq!(value.len(), N * 2);
        let mut result = [0_u8; N];
        for (index, slot) in result.iter_mut().enumerate() {
            *slot = u8::from_str_radix(&value[index * 2..index * 2 + 2], 16).unwrap();
        }
        result
    }

    fn base64url(value: &str) -> Vec<u8> {
        fn digit(byte: u8) -> u8 {
            match byte {
                b'A'..=b'Z' => byte - b'A',
                b'a'..=b'z' => byte - b'a' + 26,
                b'0'..=b'9' => byte - b'0' + 52,
                b'-' => 62,
                b'_' => 63,
                _ => panic!("invalid base64url test vector"),
            }
        }
        let mut output = Vec::new();
        let mut accumulator = 0_u32;
        let mut bits = 0_u32;
        for byte in value.bytes() {
            accumulator = (accumulator << 6) | u32::from(digit(byte));
            bits += 6;
            if bits >= 8 {
                bits -= 8;
                output.push((accumulator >> bits) as u8);
                accumulator &= (1_u32 << bits).saturating_sub(1);
            }
        }
        output
    }

    fn active_ip() -> IpAddr {
        let socket = UdpSocket::bind("0.0.0.0:0").unwrap();
        socket.connect("192.0.2.1:9").unwrap();
        socket.local_addr().unwrap().ip()
    }

    fn await_kind(bridge: &NativeBridgeV3, kind: u32) -> TermLinkDirectEventV2 {
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            if let Some(event) = bridge.events.poll()
                && event.kind == kind
            {
                return event;
            }
            assert!(Instant::now() < deadline, "V3 event timed out: {kind}");
            thread::yield_now();
        }
    }

    fn payload_candidates(event: &TermLinkDirectEventV2) -> TermLinkDirectCandidatesV3 {
        assert_eq!(
            event.payload_length as usize,
            size_of::<TermLinkDirectCandidatesV3>()
        );
        unsafe {
            std::ptr::read_unaligned(event.payload.as_ptr().cast::<TermLinkDirectCandidatesV3>())
        }
    }
}
