use std::{
    ffi::c_void,
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, Receiver, SyncSender, TrySendError},
    },
    thread::{self, JoinHandle},
};

use termlink_direct_path::{DirectEvent, DirectFailureReason, PathState, ShadowPathMachine};

mod v2;
pub use v2::*;
mod v3;
pub use v3::*;
mod v4;
pub use v4::*;

pub const MAX_QUEUE_CAPACITY: usize = 256;
pub const ABI_VERSION_V1: u32 = 1;

/// FFI v1 is a bounded path-state command/event boundary. It deliberately does
/// not accept Relay handles, realtime sockets, terminal sessions, renderers,
/// Envelope bytes, or application ACKs. Socket workflow commands will be added
/// only with fixed-size owned configuration records; callers retain no buffers.
/// Handles require external synchronization: no submit/poll call may race with
/// destroy. Poll is non-blocking; destroy joins only the state worker, whose
/// loop performs no network I/O or unbounded wait.

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NativeCommand {
    Begin { epoch: u64 },
    Probing { epoch: u64 },
    Authenticating { epoch: u64 },
    Fail { reason: DirectFailureReason },
    NetworkChanged,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SubmitError {
    QueueFull,
    WorkerStopped,
}

pub struct NativeBridge {
    commands: Option<SyncSender<NativeCommand>>,
    events: Receiver<DirectEvent>,
    abort_direct: Arc<AtomicBool>,
    overflow_event: Arc<AtomicBool>,
    relay_only: Arc<AtomicBool>,
    authoritative_epoch: Arc<AtomicU64>,
    worker: Option<JoinHandle<()>>,
}

impl NativeBridge {
    pub fn new(enabled: bool, capacity: usize) -> Result<Self, SubmitError> {
        if capacity == 0 || capacity > MAX_QUEUE_CAPACITY {
            return Err(SubmitError::QueueFull);
        }
        let (command_tx, command_rx) = mpsc::sync_channel(capacity);
        let (event_tx, event_rx) = mpsc::sync_channel(capacity);
        let abort_direct = Arc::new(AtomicBool::new(false));
        let overflow_event = Arc::new(AtomicBool::new(false));
        let relay_only = Arc::new(AtomicBool::new(true));
        let authoritative_epoch = Arc::new(AtomicU64::new(0));
        let worker_abort = Arc::clone(&abort_direct);
        let worker_overflow = Arc::clone(&overflow_event);
        let worker_relay_only = Arc::clone(&relay_only);
        let worker_epoch = Arc::clone(&authoritative_epoch);
        let worker = thread::Builder::new()
            .name("termlink-shadow-direct".into())
            .spawn(move || {
                worker_loop(
                    enabled,
                    command_rx,
                    event_tx,
                    worker_abort,
                    worker_overflow,
                    worker_relay_only,
                    worker_epoch,
                )
            })
            .map_err(|_| SubmitError::WorkerStopped)?;
        Ok(Self {
            commands: Some(command_tx),
            events: event_rx,
            abort_direct,
            overflow_event,
            relay_only,
            authoritative_epoch,
            worker: Some(worker),
        })
    }

    pub fn submit(&self, command: NativeCommand) -> Result<(), SubmitError> {
        let sender = self.commands.as_ref().ok_or(SubmitError::WorkerStopped)?;
        sender.try_send(command).map_err(|error| match error {
            TrySendError::Full(_) => {
                self.relay_only.store(true, Ordering::Release);
                self.abort_direct.store(true, Ordering::Release);
                self.overflow_event.store(true, Ordering::Release);
                SubmitError::QueueFull
            }
            TrySendError::Disconnected(_) => {
                self.relay_only.store(true, Ordering::Release);
                SubmitError::WorkerStopped
            }
        })
    }

    pub fn poll_event(&self) -> Option<DirectEvent> {
        if self.overflow_event.swap(false, Ordering::AcqRel) {
            while self.events.try_recv().is_ok() {}
            return Some(DirectEvent::DirectFailed {
                epoch: self.authoritative_epoch.load(Ordering::Acquire),
                reason: DirectFailureReason::QueueFull,
            });
        }
        self.events.try_recv().ok()
    }

    pub fn is_relay_only(&self) -> bool {
        self.relay_only.load(Ordering::Acquire)
    }
}

impl Drop for NativeBridge {
    fn drop(&mut self) {
        self.commands.take();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn worker_loop(
    enabled: bool,
    commands: Receiver<NativeCommand>,
    events: SyncSender<DirectEvent>,
    abort_direct: Arc<AtomicBool>,
    overflow_event: Arc<AtomicBool>,
    relay_only: Arc<AtomicBool>,
    authoritative_epoch: Arc<AtomicU64>,
) {
    let mut machine = ShadowPathMachine::new(enabled);
    while let Ok(command) = commands.recv() {
        if abort_direct.swap(false, Ordering::AcqRel) {
            let _ = machine.fail(DirectFailureReason::QueueFull);
            relay_only.store(true, Ordering::Release);
            while commands.try_recv().is_ok() {}
            continue;
        }
        let event = match command {
            NativeCommand::Begin { epoch } => machine.begin(epoch),
            NativeCommand::Probing { epoch } => machine.probing(epoch),
            NativeCommand::Authenticating { epoch } => machine.authenticating(epoch),
            NativeCommand::Fail { reason } => machine.fail(reason),
            NativeCommand::NetworkChanged => machine.network_changed(),
        };
        authoritative_epoch.store(machine.epoch(), Ordering::Release);
        relay_only.store(machine.state() == PathState::RelayOnly, Ordering::Release);
        if events.try_send(event).is_err() {
            // A full consumer queue is a direct-lane failure only. Never block the
            // worker and never call back into JVM/CLR from this thread.
            let _ = machine.fail(DirectFailureReason::QueueFull);
            relay_only.store(true, Ordering::Release);
            abort_direct.store(true, Ordering::Release);
            overflow_event.store(true, Ordering::Release);
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TermLinkDirectEventV1 {
    pub kind: u32,
    pub state: u32,
    pub reason: u32,
    pub epoch: u64,
}

impl From<DirectEvent> for TermLinkDirectEventV1 {
    fn from(event: DirectEvent) -> Self {
        match event {
            DirectEvent::StateChanged { state, epoch } => Self {
                kind: 1,
                state: state_code(state),
                reason: 0,
                epoch,
            },
            DirectEvent::DirectFailed { epoch, reason } => Self {
                kind: 2,
                state: state_code(PathState::RelayOnly),
                reason: reason_code(reason),
                epoch,
            },
            DirectEvent::ShadowReady { epoch, .. } => Self {
                kind: 3,
                state: state_code(PathState::ShadowDirect),
                reason: 0,
                epoch,
            },
            DirectEvent::IgnoredStale {
                received_epoch: _,
                current_epoch,
            } => Self {
                kind: 4,
                state: 0,
                reason: 0,
                epoch: current_epoch,
            },
        }
    }
}

fn state_code(state: PathState) -> u32 {
    match state {
        PathState::RelayOnly => 1,
        PathState::Gathering => 2,
        PathState::Probing => 3,
        PathState::Authenticating => 4,
        PathState::ShadowDirect => 5,
    }
}

fn reason_code(reason: DirectFailureReason) -> u32 {
    match reason {
        DirectFailureReason::Disabled => 1,
        DirectFailureReason::InvalidEpoch => 2,
        DirectFailureReason::Replay => 3,
        DirectFailureReason::Authentication => 4,
        DirectFailureReason::Timeout => 5,
        DirectFailureReason::Socket => 6,
        DirectFailureReason::Quic => 7,
        DirectFailureReason::PeerProof => 8,
        DirectFailureReason::QueueFull => 9,
        DirectFailureReason::NetworkChanged => 10,
        DirectFailureReason::Internal => 11,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn termlink_direct_create_v1(enabled: u32, capacity: u32) -> *mut c_void {
    catch_unwind(AssertUnwindSafe(|| {
        if enabled > 1 {
            return std::ptr::null_mut();
        }
        match NativeBridge::new(enabled == 1, capacity as usize) {
            Ok(bridge) => Box::into_raw(Box::new(bridge)).cast(),
            Err(_) => std::ptr::null_mut(),
        }
    }))
    .unwrap_or(std::ptr::null_mut())
}

#[unsafe(no_mangle)]
pub extern "C" fn termlink_direct_abi_version_v1() -> u32 {
    ABI_VERSION_V1
}

/// # Safety
/// `handle` must be a live pointer returned by `termlink_direct_create_v1` and
/// must not be used again after this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn termlink_direct_destroy_v1(handle: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !handle.is_null() {
            // SAFETY: Required by this function's contract and checked for null above.
            drop(unsafe { Box::from_raw(handle.cast::<NativeBridge>()) });
        }
    }));
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be a live pointer returned by `termlink_direct_create_v1`.
pub unsafe extern "C" fn termlink_direct_begin_v1(handle: *mut c_void, epoch: u64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: Required by this function's contract.
        unsafe { bridge_ref(handle) }
            .and_then(|bridge| bridge.submit(NativeCommand::Begin { epoch }).ok())
            .map_or(-1, |()| 0)
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be a live pointer returned by `termlink_direct_create_v1`.
pub unsafe extern "C" fn termlink_direct_fail_v1(handle: *mut c_void, reason: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(reason) = decode_reason(reason) else {
            return -2;
        };
        // SAFETY: Required by this function's contract.
        unsafe { bridge_ref(handle) }
            .and_then(|bridge| bridge.submit(NativeCommand::Fail { reason }).ok())
            .map_or(-1, |()| 0)
    }))
    .unwrap_or(-3)
}

#[unsafe(no_mangle)]
/// # Safety
/// `handle` must be a live pointer returned by `termlink_direct_create_v1` and
/// `output` must be writable for one `TermLinkDirectEventV1` for this call.
pub unsafe extern "C" fn termlink_direct_poll_event_v1(
    handle: *mut c_void,
    output: *mut TermLinkDirectEventV1,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if output.is_null() {
            return -1;
        }
        // SAFETY: Required by this function's contract.
        let Some(event) = (unsafe { bridge_ref(handle) }).and_then(NativeBridge::poll_event) else {
            return 0;
        };
        // SAFETY: The caller supplies a non-null writable event pointer for this call.
        unsafe { output.write(event.into()) };
        1
    }))
    .unwrap_or(-3)
}

unsafe fn bridge_ref<'a>(handle: *mut c_void) -> Option<&'a NativeBridge> {
    if handle.is_null() {
        None
    } else {
        // SAFETY: FFI callers must pass a live handle created by this library.
        Some(unsafe { &*handle.cast::<NativeBridge>() })
    }
}

fn decode_reason(reason: u32) -> Option<DirectFailureReason> {
    Some(match reason {
        1 => DirectFailureReason::Disabled,
        2 => DirectFailureReason::InvalidEpoch,
        3 => DirectFailureReason::Replay,
        4 => DirectFailureReason::Authentication,
        5 => DirectFailureReason::Timeout,
        6 => DirectFailureReason::Socket,
        7 => DirectFailureReason::Quic,
        8 => DirectFailureReason::PeerProof,
        9 => DirectFailureReason::QueueFull,
        10 => DirectFailureReason::NetworkChanged,
        11 => DirectFailureReason::Internal,
        _ => return None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    fn await_event(bridge: &NativeBridge) -> DirectEvent {
        let deadline = Instant::now() + Duration::from_secs(1);
        loop {
            if let Some(event) = bridge.poll_event() {
                return event;
            }
            assert!(Instant::now() < deadline, "native event timed out");
            thread::yield_now();
        }
    }

    #[test]
    fn worker_uses_poll_queue_instead_of_platform_callback() {
        let bridge = NativeBridge::new(true, 4).unwrap();
        bridge.submit(NativeCommand::Begin { epoch: 1 }).unwrap();
        assert!(matches!(
            await_event(&bridge),
            DirectEvent::StateChanged {
                state: PathState::Gathering,
                epoch: 1
            }
        ));
    }

    #[test]
    fn direct_failure_event_cannot_encode_disconnect_or_durable_ack() {
        let bridge = NativeBridge::new(true, 4).unwrap();
        bridge
            .submit(NativeCommand::Fail {
                reason: DirectFailureReason::Quic,
            })
            .unwrap();
        let encoded = TermLinkDirectEventV1::from(await_event(&bridge));
        assert_eq!(encoded.kind, 2);
        assert_eq!(encoded.state, 1);
        assert_eq!(encoded.reason, 7);
    }

    #[test]
    fn invalid_queue_capacity_is_bounded() {
        assert!(matches!(
            NativeBridge::new(true, MAX_QUEUE_CAPACITY + 1),
            Err(SubmitError::QueueFull)
        ));
    }

    #[test]
    fn event_queue_overflow_latches_relay_only_before_more_direct_work() {
        let bridge = NativeBridge::new(true, 1).unwrap();
        bridge.submit(NativeCommand::Begin { epoch: 1 }).unwrap();
        assert!(matches!(
            await_event(&bridge),
            DirectEvent::StateChanged {
                state: PathState::Gathering,
                ..
            }
        ));
        assert!(!bridge.is_relay_only());
        let _ = bridge.submit(NativeCommand::Probing { epoch: 1 });
        let _ = bridge.submit(NativeCommand::Authenticating { epoch: 1 });
        let deadline = Instant::now() + Duration::from_secs(1);
        while !bridge.is_relay_only() {
            assert!(
                Instant::now() < deadline,
                "overflow did not retire direct state"
            );
            thread::yield_now();
        }
        let event = await_event(&bridge);
        assert!(matches!(
            event,
            DirectEvent::DirectFailed {
                reason: DirectFailureReason::QueueFull,
                ..
            }
        ));
    }
}
