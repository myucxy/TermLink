use super::*;

fn output(terminal: &mut Terminal, sequence: u64, bytes: &[u8]) -> Result<(), TerminalError> {
    terminal.apply_event(Event::Output {
        source_sequence: sequence,
        bytes,
    })
}

fn c1_terminal(columns: u32, rows: u32) -> Terminal {
    Terminal::new_with_output_profile(
        columns,
        rows,
        0,
        TerminalOptions::default(),
        OutputProfile::Utf8WithStandaloneC1,
    )
    .expect("standalone-C1 terminal")
}

fn output_one_byte_events(terminal: &mut Terminal, bytes: &[u8]) -> Result<(), TerminalError> {
    for (sequence, byte) in bytes.iter().copied().enumerate() {
        output(
            terminal,
            u64::try_from(sequence).expect("test sequence fits u64"),
            std::slice::from_ref(&byte),
        )?;
    }
    Ok(())
}

fn u32_at(bytes: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("u32 field"))
}

fn u64_at(bytes: &[u8], offset: usize) -> u64 {
    u64::from_le_bytes(bytes[offset..offset + 8].try_into().expect("u64 field"))
}

fn host_effect_payloads(frame: &[u8]) -> Vec<&[u8]> {
    let count = usize::try_from(u32_at(frame, 16)).expect("record count");
    let payload_offset = usize::try_from(u64_at(frame, 32)).expect("payload offset");
    (0..count)
        .map(|index| {
            let record = 64 + index * 40;
            let offset = usize::try_from(u64_at(frame, record + 24)).expect("record offset");
            let length = usize::try_from(u32_at(frame, record + 32)).expect("payload length");
            assert!(offset >= payload_offset);
            &frame[offset..offset + length]
        })
        .collect()
}

#[test]
fn configured_main_scrollback_captures_output_and_keeps_public_cells_viewport_relative() {
    let mut terminal = Terminal::new_with_options(
        3,
        2,
        0,
        TerminalOptions {
            history_capacity_rows: 2,
        },
    )
    .expect("history terminal");

    output(&mut terminal, 0, b"A\r\nB\r\nC").expect("full-screen output scroll");

    assert_eq!(terminal.main.viewport_top(), 1);
    assert_eq!(terminal.main.row_metadata[0].sealed_revision, 1);
    assert_eq!(terminal.cell(0, 0).expect("visible B").character, 'B');
    assert_eq!(terminal.cell(1, 0).expect("visible C").character, 'C');
    assert_eq!(terminal.alternate.viewport_top(), 0);
}

#[test]
fn hard_line_feed_detaches_the_existing_soft_wrapped_suffix() {
    let mut terminal = Terminal::new_with_options(
        4,
        3,
        0,
        TerminalOptions {
            history_capacity_rows: 4,
        },
    )
    .expect("history terminal");

    output(&mut terminal, 0, b"ABCDE").expect("create one soft-wrapped suffix");
    let original_line_id = terminal.main.row_metadata[0].logical_line_id;
    assert!(terminal.main.soft_wrapped[0]);
    assert_eq!(
        terminal.main.row_metadata[1].logical_line_id,
        original_line_id
    );
    assert_eq!(terminal.main.row_metadata[1].logical_cell_offset, 4);
    terminal
        .export_snapshot_v2()
        .expect("soft-wrapped source is canonical");

    output(&mut terminal, 1, b"\x1b[H\r\n")
        .expect("hard LF crosses the existing soft-wrap boundary");

    assert!(!terminal.main.soft_wrapped[0]);
    assert_ne!(
        terminal.main.row_metadata[1].logical_line_id,
        original_line_id
    );
    assert_eq!(terminal.main.row_metadata[1].logical_cell_offset, 0);
    terminal
        .export_snapshot_v2()
        .expect("hard LF result remains snapshot canonical");
    terminal
        .export_render_frame_v3()
        .expect("hard LF result remains renderable");
}

#[test]
fn event_boundary_snaps_saved_cursor_when_output_replaces_its_cell_with_a_wide_lead() {
    let mut terminal = Terminal::new(10, 4).expect("terminal");
    output(&mut terminal, 0, b"\x1b[2;2H\x1b7").expect("save the initial narrow cell");
    assert_eq!(terminal.main.saved.column, 1);

    output(&mut terminal, 1, b"\x1b[2;1H\xe7\x95\x8c")
        .expect("write a wide cell across the saved coordinate");

    assert_eq!(terminal.main.saved.column, 0);
    let snapshot = terminal.export_snapshot_v2().expect("canonical checkpoint");
    let restored = Terminal::import_snapshot_v2(&snapshot).expect("checkpoint roundtrip");
    assert_eq!(restored.main.saved.column, 0);
}

#[test]
fn wrapped_boundary_state_machine_remains_canonical_across_reflow_regions_and_retention() {
    let mut terminal = Terminal::new_with_options(
        7,
        4,
        0,
        TerminalOptions {
            history_capacity_rows: 7,
        },
    )
    .expect("state-machine terminal");
    let mut random = 0x7a6d_4c39_8f21_b5e3_u64;

    for sequence in 0_u64..160 {
        random ^= random << 13;
        random ^= random >> 7;
        random ^= random << 17;
        let (columns, rows) = terminal.dimensions();
        match random % 8 {
            0 => {
                let text = format!("W{sequence:03}-ABCDE界FGHIJ");
                output(&mut terminal, sequence, text.as_bytes()).expect("wrapped text");
            }
            1 => {
                let row = 1 + u32::try_from(random % u64::from(rows)).expect("bounded row");
                let hard_lf = format!("\u{1b}[{row};1H\r\n");
                output(&mut terminal, sequence, hard_lf.as_bytes()).expect("hard line feed");
            }
            2 if rows >= 3 => {
                let region_bottom = rows - 1;
                let partial_scroll =
                    format!("\u{1b}[1;{region_bottom}r\u{1b}[{region_bottom};1H\r\n\u{1b}[r");
                output(&mut terminal, sequence, partial_scroll.as_bytes())
                    .expect("partial vertical scroll");
            }
            3 if rows >= 3 && columns >= 4 => {
                let region_bottom = rows - 1;
                let region_right = columns - 1;
                let rectangle = format!(
                    "\u{1b}[?69h\u{1b}[1;{region_bottom}r\u{1b}[2;{region_right}s\
                     \u{1b}[{region_bottom};{region_right}HXY\r\n\u{1b}[?69l\u{1b}[r"
                );
                output(&mut terminal, sequence, rectangle.as_bytes())
                    .expect("partial rectangle wrap and hard LF");
            }
            4 => {
                let next_columns = 3 + u32::try_from((random >> 8) % 8).expect("bounded columns");
                let next_rows = 2 + u32::try_from((random >> 16) % 5).expect("bounded rows");
                terminal
                    .apply_event(Event::Resize {
                        source_sequence: sequence,
                        columns: next_columns,
                        rows: next_rows,
                    })
                    .expect("state-machine reflow");
            }
            5 if rows >= 3 => {
                let region_bottom = rows - 1;
                let row_edits =
                    format!("\u{1b}[1;{region_bottom}r\u{1b}[1;1H\u{1b}[L\u{1b}[M\u{1b}[r");
                output(&mut terminal, sequence, row_edits.as_bytes()).expect("bounded row shifts");
            }
            6 => {
                let history = format!("\u{1b}[{rows};1H\r\nH{sequence:03}\r\nT{sequence:03}");
                output(&mut terminal, sequence, history.as_bytes()).expect("history retention");
            }
            _ => {
                output(&mut terminal, sequence, b"\x1b[HABCD\x1b[2K").expect("wrap cancellation");
            }
        }

        terminal.main.validate().unwrap_or_else(|error| {
            panic!("main screen is noncanonical after sequence {sequence}: {error}")
        });
        terminal.alternate.validate().unwrap_or_else(|error| {
            panic!("alternate screen is noncanonical after sequence {sequence}: {error}")
        });
        let snapshot = terminal
            .export_snapshot_v2()
            .unwrap_or_else(|error| panic!("snapshot failed after sequence {sequence}: {error}"));
        let restored = Terminal::import_snapshot_v2(&snapshot).unwrap_or_else(|error| {
            panic!("snapshot restore failed after sequence {sequence}: {error}")
        });
        assert_eq!(
            restored
                .export_snapshot_v2()
                .expect("canonical snapshot re-export"),
            snapshot,
            "snapshot bytes changed after sequence {sequence}"
        );
        assert_eq!(
            restored.export_render_frame_v3(),
            terminal.export_render_frame_v3(),
            "live frame changed after sequence {sequence}"
        );
        let available = terminal.scrollback_info().0;
        for offset in [0, available / 2, available] {
            assert_eq!(
                restored.export_render_frame_v3_at_scrollback(offset),
                terminal.export_render_frame_v3_at_scrollback(offset),
                "history frame changed at offset {offset} after sequence {sequence}"
            );
        }
        terminal = restored;
    }
}

#[test]
fn renderer_scrollback_window_and_generation_follow_output_without_changing_live_cells() {
    let mut terminal = Terminal::new_with_options(
        3,
        2,
        0,
        TerminalOptions {
            history_capacity_rows: 2,
        },
    )
    .expect("history terminal");

    output(&mut terminal, 0, b"A\r\nB\r\nC").expect("first captured row");
    assert_eq!(terminal.scrollback_info(), (1, 1));
    let first_window = terminal.main.render_window(1);
    assert_eq!(first_window.visible_cells()[0].character, 'A');
    assert_eq!(first_window.visible_cells()[3].character, 'B');
    assert_eq!(terminal.cell(0, 0).expect("live B").character, 'B');
    assert_eq!(terminal.cell(1, 0).expect("live C").character, 'C');

    output(&mut terminal, 1, b"\r\nD\r\nE").expect("history reaches capacity");
    assert_eq!(terminal.scrollback_info(), (2, 3));
    let oldest = terminal.main.render_window(u32::MAX);
    assert_eq!(oldest.visible_cells()[0].character, 'B');
    assert_eq!(oldest.visible_cells()[3].character, 'C');

    let history_frame = terminal
        .export_render_frame_v3_at_scrollback(2)
        .expect("history render frame");
    let expected_history_frame = {
        let mut projection = terminal.clone();
        projection.cursor_visible = false;
        *projection.active_screen_mut() = terminal.main.render_window(2);
        projection
            .export_render_frame_v3()
            .expect("cloned history projection")
    };
    assert_eq!(history_frame, expected_history_frame);
    assert_eq!(
        u32_at(&history_frame, 56) & 1,
        0,
        "history cursor is hidden"
    );
    assert_eq!(terminal.cell(0, 0).expect("live D").character, 'D');
    assert_eq!(terminal.cell(1, 0).expect("live E").character, 'E');
}

#[test]
fn snapshot_restores_canonical_scrollback_after_unicode_reflow_and_alternate_screen() {
    let mut terminal = Terminal::new_with_options(
        8,
        3,
        0,
        TerminalOptions {
            history_capacity_rows: 24,
        },
    )
    .expect("history terminal");
    output(
        &mut terminal,
        0,
        "\u{1b}]8;;https://history.test\u{1b}\\\u{1b}[38;5;81mA\u{e0b0}e\u{301}\u{754c}\u{1b}[0m\u{1b}]8;;\u{1b}\\\r\nsecond\r\nthird\r\nfourth\r\nfifth\r\nsixth"
            .as_bytes(),
    )
    .expect("unicode scrollback output");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 5,
            rows: 3,
        })
        .expect("history reflow");
    output(&mut terminal, 2, b"\r\nseventh\r\neighth").expect("post-reflow output");

    let (available, generation) = terminal.scrollback_info();
    assert!(available > 0);
    assert!(generation > 0);
    let history_cells = usize::try_from(available * terminal.columns).expect("bounded history");
    assert!(
        terminal.main.cells[..history_cells]
            .iter()
            .any(|cell| cell.character == '\u{e0b0}')
    );
    assert!(
        terminal.main.cells[..history_cells]
            .iter()
            .any(|cell| cell.character == '\u{754c}')
    );
    assert!(
        terminal.main.cells[..history_cells]
            .iter()
            .any(|cell| cell.text() == "e\u{301}")
    );
    assert!(
        terminal.main.cells[..history_cells]
            .iter()
            .any(|cell| cell.hyperlink.is_some())
    );

    let snapshot = terminal.export_snapshot_v2().expect("history checkpoint");
    let restored = Terminal::import_snapshot_v2(&snapshot).expect("history restore");
    assert_eq!(restored.scrollback_info(), terminal.scrollback_info());
    assert_eq!(restored.main.cells, terminal.main.cells);
    assert_eq!(restored.main.soft_wrapped, terminal.main.soft_wrapped);
    assert_eq!(restored.main.meaningful_end, terminal.main.meaningful_end);
    assert_eq!(restored.main.row_metadata, terminal.main.row_metadata);
    assert_eq!(
        restored.export_snapshot_v2().expect("canonical re-export"),
        snapshot
    );
    for offset in 0..=available {
        assert_eq!(
            restored.export_render_frame_v3_at_scrollback(offset),
            terminal.export_render_frame_v3_at_scrollback(offset),
            "offset {offset}"
        );
    }
    assert_eq!(
        restored
            .export_render_frame_v3_at_scrollback(available)
            .expect("restored oldest frame"),
        terminal
            .export_render_frame_v3_at_scrollback(available)
            .expect("original oldest frame")
    );

    output(&mut terminal, 3, b"\x1b[?1049hALT").expect("enter alternate screen");
    let alternate_snapshot = terminal
        .export_snapshot_v2()
        .expect("alternate-active checkpoint");
    let mut alternate_restored =
        Terminal::import_snapshot_v2(&alternate_snapshot).expect("restore alternate screen");
    assert!(alternate_restored.active_alternate);
    assert_eq!(
        alternate_restored.scrollback_info(),
        terminal.scrollback_info()
    );
    assert_eq!(
        alternate_restored
            .export_render_frame_v3()
            .expect("restored alternate frame"),
        terminal
            .export_render_frame_v3()
            .expect("original alternate frame")
    );
    output(&mut alternate_restored, 4, b"\x1b[?1049l").expect("leave restored alternate");
    output(&mut terminal, 4, b"\x1b[?1049l").expect("leave original alternate");
    assert_eq!(
        alternate_restored.scrollback_info(),
        terminal.scrollback_info()
    );
    assert_eq!(
        alternate_restored
            .export_render_frame_v3_at_scrollback(available)
            .expect("restored oldest frame"),
        terminal
            .export_render_frame_v3_at_scrollback(available)
            .expect("original oldest frame")
    );
}

#[test]
fn erase_display_three_clears_only_scrollback_and_keeps_history_policy() {
    let mut terminal = Terminal::new_with_options(
        3,
        2,
        0,
        TerminalOptions {
            history_capacity_rows: 2,
        },
    )
    .expect("history terminal");
    output(&mut terminal, 0, b"A\r\nB\r\nC").expect("capture history");
    assert_eq!(terminal.main.viewport_top(), 1);

    output(&mut terminal, 1, b"\x1b[3J").expect("ED3");
    assert_eq!(terminal.main.viewport_top(), 0);
    assert_eq!(terminal.main.history_capacity(), 2);
    assert_eq!(terminal.cell(0, 0).expect("visible B").character, 'B');
    assert_eq!(terminal.cell(1, 0).expect("visible C").character, 'C');

    output(&mut terminal, 2, b"\r\nD").expect("history resumes after ED3");
    assert_eq!(terminal.main.viewport_top(), 1);
    assert_eq!(terminal.cell(0, 0).expect("visible C").character, 'C');
    assert_eq!(terminal.cell(1, 0).expect("visible D").character, 'D');
}

#[test]
fn scrollback_revision_is_per_event_and_atomic_batch_failure_rolls_it_back() {
    let options = TerminalOptions {
        history_capacity_rows: 2,
    };
    let mut terminal = Terminal::new_with_options(3, 2, 0, options).expect("history terminal");
    output(&mut terminal, 0, b"A\r\nB").expect("fill visible rows");
    output(&mut terminal, 1, b"\r\nC").expect("capture first history line");
    assert_eq!(terminal.main.row_metadata[0].sealed_revision, 2);

    let before = terminal.clone();
    let events = [
        Event::Output {
            source_sequence: 2,
            bytes: b"\r\nD",
        },
        Event::Resize {
            source_sequence: 3,
            columns: 0,
            rows: 2,
        },
    ];
    assert_eq!(
        terminal.apply_events_atomic(&events),
        Err(TerminalError::InvalidDimensions)
    );
    assert_eq!(terminal, before);
}

fn row_text(terminal: &Terminal, row: u32) -> String {
    (0..terminal.columns)
        .map(|column| {
            let cell = terminal.cell(row, column).expect("cell in bounds");
            if cell.continuation {
                '·'
            } else {
                cell.character
            }
        })
        .collect()
}

type CursorAliasCase<'a> = (&'a [u8], &'a [u8], (u32, u32));

fn assert_snapshot_self_closure(terminal: &Terminal) {
    let snapshot = terminal.export_snapshot_v2().expect("eligible snapshot");
    let restored = Terminal::import_snapshot_v2(&snapshot).expect("self-import snapshot");
    assert_eq!(&restored, terminal);
    assert_eq!(
        restored.export_snapshot_v2().expect("canonical export"),
        snapshot
    );
}

fn assert_snapshot_mutation_is_malformed(
    snapshot: &[u8],
    description: &str,
    mutate: impl FnOnce(&mut [u8]),
) {
    let mut malformed = snapshot.to_vec();
    mutate(&mut malformed);
    snapshot::refresh_crc_for_test(&mut malformed);
    assert_eq!(
        Terminal::import_snapshot_v2(&malformed),
        Err(TerminalError::MalformedSnapshot),
        "{description}"
    );
}

#[test]
fn dimensions_and_initial_sequence_are_strictly_validated() {
    assert_eq!(Terminal::new(0, 1), Err(TerminalError::InvalidDimensions));
    assert_eq!(Terminal::new(1, 0), Err(TerminalError::InvalidDimensions));
    assert_eq!(
        Terminal::new(MAX_COLUMNS + 1, 1),
        Err(TerminalError::LimitExceeded)
    );
    assert_eq!(
        Terminal::new_with_sequence(8, 2, u64::MAX),
        Err(TerminalError::SequenceExhausted)
    );
    assert_eq!(
        Terminal::new_with_sequence(8, 2, u64::MAX - 1),
        Err(TerminalError::SequenceExhausted)
    );
    let mut terminal = Terminal::new_with_sequence(8, 2, 1).expect("durable frame origin");
    output(&mut terminal, 1, b"frame-one").expect("initial sequence is accepted");
    assert_eq!(terminal.next_source_sequence(), 2);
}

#[test]
fn sequence_exhaustion_never_creates_an_unrestorable_next_sequence() {
    let mut terminal =
        Terminal::new_with_sequence(4, 2, u64::MAX - 2).expect("last consumable sequence");
    output(&mut terminal, u64::MAX - 2, b"x").expect("last event before exhaustion");
    assert_eq!(terminal.next_source_sequence(), u64::MAX - 1);
    assert_snapshot_self_closure(&terminal);
    assert_eq!(
        output(&mut terminal, u64::MAX - 1, b"y"),
        Err(TerminalError::SequenceExhausted)
    );
    assert!(!terminal.is_failed_closed());
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn stale_is_retryable_but_jump_and_any_gap_poison() {
    let mut stale = Terminal::new(8, 2).expect("terminal");
    output(&mut stale, 0, b"a").expect("first event");
    assert_eq!(
        output(&mut stale, 0, b"duplicate"),
        Err(TerminalError::SequenceMismatch {
            expected: 1,
            actual: 0,
        })
    );
    assert!(!stale.is_failed_closed());
    output(&mut stale, 1, b"b").expect("stale mismatch does not poison");

    let mut jump = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut jump, 4, b"jump"),
        Err(TerminalError::SequenceMismatch {
            expected: 0,
            actual: 4,
        })
    );
    assert!(jump.is_failed_closed());
    assert_eq!(output(&mut jump, 0, b"x"), Err(TerminalError::FailedClosed));
    assert_eq!(jump.export_snapshot_v2(), Err(TerminalError::FailedClosed));

    for sequence in [0, 7] {
        let mut gap = Terminal::new(8, 2).expect("terminal");
        assert_eq!(
            gap.apply_event(Event::Gap {
                source_sequence: sequence,
            }),
            Err(TerminalError::Gap)
        );
        assert!(gap.is_failed_closed());
        assert_eq!(gap.export_snapshot_v2(), Err(TerminalError::FailedClosed));
    }
    let mut stale_gap = Terminal::new(8, 2).expect("terminal");
    output(&mut stale_gap, 0, b"x").expect("advance sequence");
    assert_eq!(
        stale_gap.apply_event(Event::Gap { source_sequence: 0 }),
        Err(TerminalError::Gap)
    );
    assert!(stale_gap.is_failed_closed());
}

#[test]
fn same_sequence_validation_failure_is_atomic_and_retryable() {
    let mut terminal = Terminal::new(5, 2).expect("terminal");
    assert_eq!(
        terminal.apply_event(Event::Resize {
            source_sequence: 0,
            columns: 0,
            rows: 2,
        }),
        Err(TerminalError::InvalidDimensions)
    );
    assert_eq!(terminal.dimensions(), (5, 2));
    assert_eq!(terminal.next_source_sequence(), 0);
    output(&mut terminal, 0, b"ok").expect("retry at same sequence");
    assert_eq!(row_text(&terminal, 0), "ok   ");
}

#[test]
fn atomic_batch_matches_sequential_success_and_commits_every_event() {
    let output_bytes = b"ab";
    let events = [
        Event::Output {
            source_sequence: 0,
            bytes: output_bytes,
        },
        Event::Resize {
            source_sequence: 1,
            columns: 5,
            rows: 2,
        },
        Event::Advance {
            source_sequence: 2,
            micros: 17,
        },
    ];

    let mut batched = Terminal::new(4, 2).expect("batched terminal");
    batched
        .apply_events_atomic(&events)
        .expect("atomic batch succeeds");

    let mut sequential = Terminal::new(4, 2).expect("sequential terminal");
    for event in events {
        sequential
            .apply_event(event)
            .expect("single event succeeds");
    }

    assert_eq!(batched, sequential);
    assert_eq!(batched.next_source_sequence(), 3);
    assert_eq!(batched.logical_time_micros(), 17);
    assert_snapshot_self_closure(&batched);
}

#[test]
fn atomic_batch_discards_a_successful_prefix_after_retryable_failure() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    let before = terminal.clone();
    let events = [
        Event::Output {
            source_sequence: 0,
            bytes: b"prefix",
        },
        Event::Resize {
            source_sequence: 1,
            columns: 0,
            rows: 2,
        },
    ];

    assert_eq!(
        terminal.apply_events_atomic(&events),
        Err(TerminalError::InvalidDimensions)
    );
    assert_eq!(terminal, before);
    output(&mut terminal, 0, b"retry").expect("original sequence remains retryable");
}

#[test]
fn atomic_batch_poisoning_failures_preserve_only_the_failed_closed_latch() {
    let cases = [
        (
            Event::Output {
                source_sequence: 1,
                bytes: b"\x1b[999m",
            },
            TerminalError::Unsupported("SGR parameter"),
        ),
        (Event::Gap { source_sequence: 1 }, TerminalError::Gap),
        (
            Event::Output {
                source_sequence: 2,
                bytes: b"jump",
            },
            TerminalError::SequenceMismatch {
                expected: 1,
                actual: 2,
            },
        ),
    ];

    for (second, expected_error) in cases {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        let events = [
            Event::Output {
                source_sequence: 0,
                bytes: b"prefix",
            },
            second,
        ];
        assert_eq!(terminal.apply_events_atomic(&events), Err(expected_error));
        assert!(terminal.is_failed_closed());
        assert_eq!(terminal.next_source_sequence(), 0);
        assert_eq!(row_text(&terminal, 0), "        ");
    }
}

#[test]
fn empty_atomic_batch_is_an_exact_no_op() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    output(&mut terminal, 0, b"x").expect("baseline");
    let before = terminal.clone();
    terminal
        .apply_events_atomic(&[])
        .expect("empty batch is valid");
    assert_eq!(terminal, before);
}

#[test]
fn osc_parse_allocation_failure_is_atomic_and_retryable_at_the_same_sequence() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    let before = terminal.clone();

    parser::fail_next_osc_payload_allocation_for_test();
    assert_eq!(
        output(&mut terminal, 0, b"\x1b]2;title\x07"),
        Err(TerminalError::LimitExceeded)
    );
    assert_eq!(terminal, before);
    assert_eq!(terminal.next_source_sequence(), 0);
    assert!(!terminal.is_failed_closed());

    output(&mut terminal, 0, b"\x1b]2;title\x07").expect("retry at same source sequence");
    assert_eq!(terminal.next_source_sequence(), 1);
    assert_eq!(terminal.osc_state().window_title(), "title");
}

#[test]
fn deterministic_osc_parse_errors_remain_fail_closed() {
    let mut invalid_selector = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut invalid_selector, 0, b"\x1b]x;payload"),
        Err(TerminalError::Unsupported("OSC selector syntax"))
    );
    assert!(invalid_selector.is_failed_closed());
    assert_eq!(invalid_selector.next_source_sequence(), 0);

    let mut over_cap_bytes = b"\x1b]2;".to_vec();
    over_cap_bytes.extend(std::iter::repeat_n(b'x', 4 * 1024 + 1));
    let mut over_cap = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut over_cap, 0, &over_cap_bytes),
        Err(TerminalError::Unsupported("OSC payload limit"))
    );
    assert!(over_cap.is_failed_closed());
    assert_eq!(over_cap.next_source_sequence(), 0);
}

#[test]
fn pending_utf8_escape_and_csi_are_not_snapshot_boundaries() {
    let mut utf8 = Terminal::new(8, 2).expect("terminal");
    output(&mut utf8, 0, &[0xf0, 0x9f]).expect("UTF-8 prefix");
    assert_eq!(
        utf8.export_snapshot_v2(),
        Err(TerminalError::SnapshotNotEligible)
    );
    output(&mut utf8, 1, &[0x98, 0x80]).expect("UTF-8 suffix");
    assert_snapshot_self_closure(&utf8);

    let mut escape = Terminal::new(8, 2).expect("terminal");
    output(&mut escape, 0, b"\x1b").expect("ESC prefix");
    assert_eq!(
        escape.export_snapshot_v2(),
        Err(TerminalError::SnapshotNotEligible)
    );
    output(&mut escape, 1, b"[2J").expect("complete CSI");
    assert_snapshot_self_closure(&escape);

    let mut csi = Terminal::new(8, 2).expect("terminal");
    output(&mut csi, 0, b"\x1b[31").expect("CSI prefix");
    assert_eq!(
        csi.export_snapshot_v2(),
        Err(TerminalError::SnapshotNotEligible)
    );
    output(&mut csi, 1, b"mX").expect("complete CSI");
    assert_snapshot_self_closure(&csi);
}

#[test]
fn synchronized_output_private_mode_is_canonical_and_split_deterministic() {
    let bytes = b"\x1b[?2026hhello\x1b[?2026l";
    let mut expected_two_events = Terminal::new(12, 2).expect("terminal");
    output(&mut expected_two_events, 0, bytes).expect("joined synchronized output");
    output(&mut expected_two_events, 1, b"").expect("matching event count");
    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(12, 2).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("private-mode prefix");
        output(&mut actual, 1, &bytes[split..]).expect("private-mode suffix");
        assert_eq!(actual, expected_two_events, "split at byte {split}");
    }

    let mut expected_byte_events = Terminal::new(12, 2).expect("terminal");
    output(&mut expected_byte_events, 0, bytes).expect("joined synchronized output");
    for sequence in 1..bytes.len() {
        output(
            &mut expected_byte_events,
            u64::try_from(sequence).expect("test sequence"),
            b"",
        )
        .expect("matching empty event");
    }
    let mut bytewise = Terminal::new(12, 2).expect("terminal");
    for (sequence, byte) in bytes.iter().enumerate() {
        output(
            &mut bytewise,
            u64::try_from(sequence).expect("test sequence"),
            std::slice::from_ref(byte),
        )
        .expect("one-byte event");
    }
    assert_eq!(bytewise, expected_byte_events);

    let mut missing = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut missing, 0, b"\x1b[?h"),
        Err(TerminalError::Unsupported("empty private mode"))
    );
    assert!(missing.is_failed_closed());
    assert_eq!(missing.synchronized_output_deadline_micros(), None);
}

#[test]
fn synchronized_output_gates_all_exports_until_one_end_and_survives_other_mutations() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b[?2026h").expect("begin synchronized output");
    assert_eq!(
        terminal.synchronized_output_deadline_micros(),
        Some(1_000_000)
    );
    output(&mut terminal, 1, b"content").expect("ordinary output remains deferred");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 2,
            columns: 10,
            rows: 3,
        })
        .expect("resize remains deferred");
    output(&mut terminal, 3, b"\x1b[?1049h").expect("alternate screen remains deferred");
    assert_eq!(
        terminal.export_snapshot_v2(),
        Err(TerminalError::PresentationDeferred)
    );
    assert_eq!(
        terminal.export_render_frame_v1(),
        Err(TerminalError::PresentationDeferred)
    );
    assert_eq!(
        terminal.export_render_frame_v2(),
        Err(TerminalError::PresentationDeferred)
    );

    output(&mut terminal, 4, b"\x1b[?2026l").expect("end synchronized output");
    assert_eq!(terminal.synchronized_output_deadline_micros(), None);
    terminal.export_snapshot_v2().expect("snapshot released");
    terminal
        .export_render_frame_v1()
        .expect("frame V1 released");
    terminal
        .export_render_frame_v2()
        .expect("frame V2 released");
    output(&mut terminal, 5, b"\x1b[?2026l").expect("repeated end is idempotent");
    assert_eq!(terminal.synchronized_output_deadline_micros(), None);
}

#[test]
fn synchronized_output_deadline_uses_logical_time_exactly_and_begin_resets_it() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b[?2026h").expect("begin");
    terminal
        .apply_event(Event::Advance {
            source_sequence: 1,
            micros: 999_999,
        })
        .expect("advance below deadline");
    assert_eq!(
        terminal.synchronized_output_deadline_micros(),
        Some(1_000_000)
    );
    terminal
        .apply_event(Event::Advance {
            source_sequence: 2,
            micros: 1,
        })
        .expect("advance to deadline");
    assert_eq!(terminal.synchronized_output_deadline_micros(), None);

    output(&mut terminal, 3, b"\x1b[?2026h").expect("begin at one second");
    terminal
        .apply_event(Event::Advance {
            source_sequence: 4,
            micros: 500_000,
        })
        .expect("advance halfway");
    output(&mut terminal, 5, b"\x1b[?2026h").expect("repeat begin resets deadline");
    assert_eq!(
        terminal.synchronized_output_deadline_micros(),
        Some(2_500_000)
    );
    terminal
        .apply_event(Event::Advance {
            source_sequence: 6,
            micros: 999_999,
        })
        .expect("new deadline remains active below boundary");
    assert_eq!(
        terminal.synchronized_output_deadline_micros(),
        Some(2_500_000)
    );
    terminal
        .apply_event(Event::Advance {
            source_sequence: 7,
            micros: 1,
        })
        .expect("new deadline expires exactly");
    assert_eq!(terminal.synchronized_output_deadline_micros(), None);

    let mut saturated = Terminal::new(8, 2).expect("terminal");
    saturated
        .apply_event(Event::Advance {
            source_sequence: 0,
            micros: u64::MAX - 500_000,
        })
        .expect("advance near logical-time maximum");
    output(&mut saturated, 1, b"\x1b[?2026h").expect("saturating begin");
    assert_eq!(
        saturated.synchronized_output_deadline_micros(),
        Some(u64::MAX)
    );
    assert_eq!(
        saturated.apply_event(Event::Advance {
            source_sequence: 2,
            micros: 500_001,
        }),
        Err(TerminalError::TimeOverflow)
    );
    assert_eq!(
        saturated.synchronized_output_deadline_micros(),
        Some(u64::MAX)
    );
    saturated
        .apply_event(Event::Advance {
            source_sequence: 2,
            micros: 499_999,
        })
        .expect("one microsecond below saturated deadline");
    assert_eq!(
        saturated.synchronized_output_deadline_micros(),
        Some(u64::MAX)
    );
    saturated
        .apply_event(Event::Advance {
            source_sequence: 3,
            micros: 1,
        })
        .expect("reach saturated deadline");
    assert_eq!(saturated.synchronized_output_deadline_micros(), None);
}

#[test]
fn ris_and_inactive_reset_clear_synchronized_output_without_implicit_begin() {
    let mut inactive = Terminal::new(8, 2).expect("terminal");
    output(&mut inactive, 0, b"\x1b[?2026l").expect("reset without begin");
    assert_eq!(inactive.synchronized_output_deadline_micros(), None);
    output(&mut inactive, 1, b"\x1bc").expect("RIS without begin");
    assert_eq!(inactive.synchronized_output_deadline_micros(), None);

    let mut active = Terminal::new(8, 2).expect("terminal");
    output(&mut active, 0, b"\x1b[?2026h").expect("begin");
    output(&mut active, 1, b"\x1bc").expect("RIS clears synchronized output");
    assert_eq!(active.synchronized_output_deadline_micros(), None);
    active
        .export_snapshot_v2()
        .expect("RIS releases presentation");
}

#[test]
fn synchronized_output_batches_commit_atomically_and_private_lists_fail_as_a_unit() {
    let begin = Event::Output {
        source_sequence: 0,
        bytes: b"\x1b[?2026h",
    };
    let mut begin_only = Terminal::new(8, 2).expect("terminal");
    begin_only
        .apply_events_atomic(&[begin])
        .expect("begin-only batch");
    assert_eq!(
        begin_only.export_snapshot_v2(),
        Err(TerminalError::PresentationDeferred)
    );

    let mut complete = Terminal::new(8, 2).expect("terminal");
    complete
        .apply_events_atomic(&[
            begin,
            Event::Output {
                source_sequence: 1,
                bytes: b"content",
            },
            Event::Output {
                source_sequence: 2,
                bytes: b"\x1b[?2026l",
            },
        ])
        .expect("complete synchronized-output batch");
    assert_eq!(complete.synchronized_output_deadline_micros(), None);
    complete
        .export_snapshot_v2()
        .expect("complete batch is ready");

    let mut invalid_list = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut invalid_list, 0, b"\x1b[?2026;9999h"),
        Err(TerminalError::Unsupported("private mode"))
    );
    assert!(invalid_list.is_failed_closed());
    assert_eq!(invalid_list.next_source_sequence(), 0);
    assert_eq!(invalid_list.synchronized_output_deadline_micros(), None);
    assert_eq!(row_text(&invalid_list, 0), "        ");
}

#[test]
fn synchronized_output_preserves_stale_future_gap_and_gate_priority_semantics() {
    let begin = Event::Output {
        source_sequence: 0,
        bytes: b"\x1b[?2026h",
    };
    let mut stale = Terminal::new(8, 2).expect("terminal");
    output(&mut stale, 0, b"\x1b[?2026h").expect("begin");
    assert_eq!(
        output(&mut stale, 0, b"stale"),
        Err(TerminalError::SequenceMismatch {
            expected: 1,
            actual: 0,
        })
    );
    assert!(!stale.is_failed_closed());
    assert_eq!(stale.synchronized_output_deadline_micros(), Some(1_000_000));

    for poison in [
        Event::Output {
            source_sequence: 2,
            bytes: b"future",
        },
        Event::Gap { source_sequence: 1 },
    ] {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        assert!(terminal.apply_events_atomic(&[begin, poison]).is_err());
        assert!(terminal.is_failed_closed());
        assert_eq!(terminal.next_source_sequence(), 0);
        assert_eq!(terminal.synchronized_output_deadline_micros(), None);
        assert_eq!(
            terminal.export_snapshot_v2(),
            Err(TerminalError::FailedClosed)
        );
    }

    let mut incomplete = Terminal::new(8, 2).expect("terminal");
    output(&mut incomplete, 0, b"\x1b[?2026h").expect("begin");
    output(&mut incomplete, 1, b"\x1b").expect("incomplete escape");
    assert_eq!(
        incomplete.export_snapshot_v2(),
        Err(TerminalError::SnapshotNotEligible)
    );
    assert_eq!(
        output(&mut incomplete, 4, b"future"),
        Err(TerminalError::SequenceMismatch {
            expected: 2,
            actual: 4,
        })
    );
    assert_eq!(
        incomplete.export_snapshot_v2(),
        Err(TerminalError::FailedClosed)
    );
}

#[test]
fn decscusr_maps_all_standard_cursor_styles_and_is_split_deterministic() {
    let cases: &[(&[u8], CursorShape, bool)] = &[
        (b"\x1b[ q", CursorShape::Block, true),
        (b"\x1b[0 q", CursorShape::Block, true),
        (b"\x1b[1 q", CursorShape::Block, true),
        (b"\x1b[2 q", CursorShape::Block, false),
        (b"\x1b[3 q", CursorShape::Underline, true),
        (b"\x1b[4 q", CursorShape::Underline, false),
        (b"\x1b[5 q", CursorShape::Bar, true),
        (b"\x1b[6 q", CursorShape::Bar, false),
    ];
    for &(bytes, shape, blinking) in cases {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        output(&mut terminal, 0, bytes).expect("DECSCUSR");
        assert_eq!(terminal.cursor_shape(), shape, "{bytes:?}");
        assert_eq!(terminal.cursor_blinking(), blinking, "{bytes:?}");

        for split in 0..=bytes.len() {
            let mut expected = Terminal::new(8, 2).expect("terminal");
            output(&mut expected, 0, bytes).expect("unsplit DECSCUSR");
            output(&mut expected, 1, b"").expect("matching event count");

            let mut actual = Terminal::new(8, 2).expect("terminal");
            output(&mut actual, 0, &bytes[..split]).expect("DECSCUSR prefix");
            output(&mut actual, 1, &bytes[split..]).expect("DECSCUSR suffix");
            assert_eq!(actual, expected, "{bytes:?} split {split}");
        }
    }
}

#[test]
fn cursor_style_is_global_not_saved_by_cursors_or_screens_and_ris_resets_it() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[6 q\x1b7\x1b8\x1b[s\x1b[u\x1b[?1048h\x1b[?1048l\x1b[?47h\x1b[?47l\x1b[?1047h\x1b[?1047l\x1b[?1049h\x1b[?1049l",
    )
    .expect("saved cursors and alternate modes");
    assert_eq!(terminal.cursor_shape(), CursorShape::Bar);
    assert!(!terminal.cursor_blinking());
    assert!(!terminal.is_alternate_screen_active());

    output(&mut terminal, 1, b"\x1bc").expect("RIS");
    assert_eq!(terminal.cursor_shape(), CursorShape::Block);
    assert!(terminal.cursor_blinking());
}

#[test]
fn decscusr_rejects_noncanonical_forms_and_atomic_failures_do_not_commit_style() {
    for bytes in [
        b"\x1b[7 q".as_slice(),
        b"\x1b[1;2 q",
        b"\x1b[1:2 q",
        b"\x1b[?1 q",
    ] {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        assert!(matches!(
            output(&mut terminal, 0, bytes),
            Err(TerminalError::Unsupported(_))
        ));
        assert!(terminal.is_failed_closed(), "{bytes:?}");
        assert_eq!(terminal.cursor_shape(), CursorShape::Block, "{bytes:?}");
        assert!(terminal.cursor_blinking(), "{bytes:?}");
        assert_eq!(terminal.next_source_sequence(), 0, "{bytes:?}");
    }

    let mut terminal = Terminal::new(8, 2).expect("terminal");
    let before = terminal.clone();
    assert_eq!(
        terminal.apply_events_atomic(&[
            Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[4 q",
            },
            Event::Resize {
                source_sequence: 1,
                columns: 0,
                rows: 2,
            },
        ]),
        Err(TerminalError::InvalidDimensions)
    );
    assert_eq!(terminal, before);
}

#[test]
fn rep_defaults_zero_counts_and_replaces_the_repeat_source() {
    for sequence in [b"A\x1b[b".as_slice(), b"A\x1b[0b", b"A\x1b[1b"] {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        output(&mut terminal, 0, sequence).expect("default REP");
        assert_eq!(row_text(&terminal, 0), "AA      ");
        assert_eq!(terminal.repeat_source, None);
    }

    let mut counted = Terminal::new(8, 2).expect("terminal");
    output(&mut counted, 0, b"A\x1b[3b").expect("counted REP");
    assert_eq!(row_text(&counted, 0), "AAAA    ");

    let mut latest = Terminal::new(8, 2).expect("terminal");
    output(&mut latest, 0, b"AB\x1b[b").expect("latest graphic is repeated");
    assert_eq!(row_text(&latest, 0), "ABB     ");

    let mut missing = Terminal::new(8, 2).expect("terminal");
    output(&mut missing, 0, b"\x1b[9b").expect("REP without source is a no-op");
    assert_eq!(row_text(&missing, 0), "        ");
    assert_eq!(missing.repeat_source, None);
}

#[test]
fn rep_preserves_complete_wide_combining_and_zwj_graphemes_across_events_and_splits() {
    let mut wide = Terminal::new(12, 2).expect("terminal");
    output(&mut wide, 0, "界\x1b[2b".as_bytes()).expect("wide REP");
    for column in [0, 2, 4] {
        assert_eq!(wide.cell(0, column).expect("wide lead").text(), "界");
        assert!(
            wide.cell(0, column + 1)
                .expect("wide continuation")
                .continuation
        );
    }

    let mut combining = Terminal::new(8, 2).expect("terminal");
    output(&mut combining, 0, "e\u{301}".as_bytes()).expect("combining source");
    output(&mut combining, 1, b"\x1b[").expect("split REP prefix");
    output(&mut combining, 2, b"2b").expect("split REP suffix");
    for column in 0..3 {
        assert_eq!(
            combining.cell(0, column).expect("combined cell").text(),
            "e\u{301}"
        );
    }

    let mut joined = Terminal::new(12, 2).expect("terminal");
    output(&mut joined, 0, "👩\u{200d}".as_bytes()).expect("joined prefix");
    output(&mut joined, 1, "💻".as_bytes()).expect("joined suffix across event");
    output(&mut joined, 2, b"\x1b[2b").expect("joined REP");
    for column in [0, 2, 4] {
        assert_eq!(
            joined.cell(0, column).expect("joined cell").text(),
            "👩\u{200d}💻"
        );
    }
}

#[test]
fn rep_complete_sequence_is_equivalent_at_every_event_and_byte_split() {
    let bytes = b"A\x1b[2b";
    for split in 0..=bytes.len() {
        let mut expected = Terminal::new(8, 2).expect("terminal");
        output(&mut expected, 0, bytes).expect("unsplit REP");
        output(&mut expected, 1, b"").expect("matching event count");

        let mut actual = Terminal::new(8, 2).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("REP prefix");
        output(&mut actual, 1, &bytes[split..]).expect("REP suffix");
        assert_eq!(actual, expected, "split {split}");
    }

    let mut expected = Terminal::new(8, 2).expect("terminal");
    output(&mut expected, 0, bytes).expect("unsplit REP");
    for sequence in 1..bytes.len() as u64 {
        output(&mut expected, sequence, b"").expect("matching empty event");
    }
    let mut bytewise = Terminal::new(8, 2).expect("terminal");
    for (sequence, byte) in bytes.iter().copied().enumerate() {
        output(&mut bytewise, sequence as u64, &[byte]).expect("single REP byte event");
    }
    assert_eq!(bytewise, expected);
}

#[test]
fn rep_uses_current_presentation_without_capturing_it_in_the_source() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"A").expect("repeat source");
    let source = terminal
        .repeat_source
        .clone()
        .expect("repeat source retained");

    terminal.main.attributes.foreground = Color::Rgb(1, 2, 3);
    terminal.main.set_protected(true);
    terminal
        .osc
        .apply(8, b"id=rep;https://example.test")
        .expect("open current hyperlink");
    let hyperlink = terminal.osc.current_hyperlink();

    output(&mut terminal, 1, b"\x1b[b").expect("repeat with current presentation");
    let original = terminal.cell(0, 0).expect("original");
    let repeated = terminal.cell(0, 1).expect("repeated");
    assert_eq!(original.attributes, Attributes::default());
    assert!(!original.protected);
    assert_eq!(original.hyperlink, None);
    assert_eq!(repeated.character, source.character);
    assert_eq!(repeated.combining, source.combining);
    assert_eq!(repeated.width, source.width);
    assert_eq!(repeated.attributes.foreground, Color::Rgb(1, 2, 3));
    assert!(repeated.protected);
    assert_eq!(repeated.hyperlink, hyperlink);

    let mut insert = Terminal::new(8, 2).expect("terminal");
    output(&mut insert, 0, b"AB").expect("insert source");
    insert.main.position_cursor(0, 1);
    insert.modes.insert_mode = true;
    output(&mut insert, 1, b"\x1b[b").expect("IRM repeat");
    assert_eq!(row_text(&insert, 0), "ABB     ");

    let mut wrapping = Terminal::new(2, 2).expect("terminal");
    output(&mut wrapping, 0, b"A").expect("autowrap source");
    wrapping.main.position_cursor(0, 1);
    wrapping.modes.auto_wrap = true;
    output(&mut wrapping, 1, b"\x1b[2b").expect("DECAWM repeat");
    assert_eq!(row_text(&wrapping, 0), "AA");
    assert_eq!(row_text(&wrapping, 1), "A ");
}

#[test]
fn rep_source_clear_boundaries_preserve_only_nul_and_del() {
    let mut ignored = Terminal::new(8, 2).expect("terminal");
    output(&mut ignored, 0, b"A\0\x7f\x1b[b").expect("NUL and DEL preserve REP");
    assert_eq!(row_text(&ignored, 0), "AA      ");

    let mut ignored_in_csi = Terminal::new(8, 2).expect("terminal");
    output(&mut ignored_in_csi, 0, b"A\x1b[\0\x7fb").expect("NUL and DEL preserve a pending REP");
    assert_eq!(row_text(&ignored_in_csi, 0), "AA      ");

    for (name, bytes) in [
        ("C0", b"A\x07\x1b[b".as_slice()),
        ("ESC", b"A\x1b7\x1b[b"),
        ("CSI", b"A\x1b[0m\x1b[b"),
        ("OSC", b"A\x1b]2;title\x07\x1b[b"),
        ("ignored DCS", b"A\x1bPz\x1b\\\x1b[b"),
        ("CAN abort", b"A\x1b[12\x18\x1b[b"),
        ("SUB abort", b"A\x1b]2;discard\x1a\x1b[b"),
    ] {
        let mut terminal = Terminal::new(16, 2).expect("terminal");
        output(&mut terminal, 0, bytes).unwrap_or_else(|error| panic!("{name}: {error}"));
        assert_eq!(terminal.cell(0, 0).expect("original").character, 'A');
        assert_eq!(terminal.cell(0, 1).expect("not repeated").character, ' ');
        assert_eq!(terminal.repeat_source, None, "{name}");
    }

    let mut once = Terminal::new(8, 2).expect("terminal");
    output(&mut once, 0, b"A\x1b[b\x1b[b").expect("REP clears after completion");
    assert_eq!(row_text(&once, 0), "AA      ");
}

#[test]
fn rep_rejects_noncanonical_sequence_shapes_without_committing_the_source() {
    for bytes in [
        b"\x1b[?1b".as_slice(),
        b"\x1b[1;2b",
        b"\x1b[1:2b",
        b"\x1b[1$b",
    ] {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        output(&mut terminal, 0, b"A").expect("repeat source");
        let before = terminal.clone();
        assert!(output(&mut terminal, 1, bytes).is_err());
        assert!(terminal.is_failed_closed());
        assert_eq!(terminal.repeat_source, before.repeat_source);
        assert_eq!(row_text(&terminal, 0), row_text(&before, 0));
    }
}

#[test]
fn rep_source_roundtrips_for_snapshot_suffix_and_atomic_batch_rollback() {
    for source in ["A", "界", "e\u{301}", "👩\u{200d}💻"] {
        let mut direct = Terminal::new(24, 2).expect("terminal");
        output(&mut direct, 0, source.as_bytes()).expect("repeat source");
        let checkpoint = direct.export_snapshot_v2().expect("REP checkpoint");
        let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("restore REP source");
        output(&mut direct, 1, b"\x1b[2b").expect("direct suffix");
        output(&mut restored, 1, b"\x1b[2b").expect("restored suffix");
        assert_eq!(restored, direct, "source {source:?}");
        assert_eq!(
            restored.export_snapshot_v2().expect("restored final"),
            direct.export_snapshot_v2().expect("direct final"),
            "source {source:?}"
        );
    }

    let mut atomic = Terminal::new(8, 2).expect("terminal");
    output(&mut atomic, 0, b"A").expect("repeat source");
    let before = atomic.clone();
    let events = [
        Event::Output {
            source_sequence: 1,
            bytes: b"\x1b[2b",
        },
        Event::Resize {
            source_sequence: 2,
            columns: 0,
            rows: 2,
        },
    ];
    assert_eq!(
        atomic.apply_events_atomic(&events),
        Err(TerminalError::InvalidDimensions)
    );
    assert_eq!(atomic, before);
    output(&mut atomic, 1, b"\x1b[b").expect("retry uses rolled-back source");
    assert_eq!(row_text(&atomic, 0), "AA      ");
}

#[test]
fn import_rejects_a_noncanonical_section_count() {
    let terminal = Terminal::new(4, 2).expect("terminal");
    let mut snapshot = terminal.export_snapshot_v2().expect("snapshot");
    snapshot[40] = 1;
    assert_eq!(
        Terminal::import_snapshot_v2(&snapshot),
        Err(TerminalError::IncompatibleSnapshot)
    );
}

#[test]
fn c1_control_fails_closed_without_creating_noncanonical_cells() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut terminal, 0, &[b'A', 0xc2, 0x80, b'B']),
        Err(TerminalError::Unsupported("C1 or Unicode control"))
    );
    assert!(terminal.is_failed_closed());
    assert_eq!(row_text(&terminal, 0), "        ");
    assert_eq!(
        terminal.export_snapshot_v2(),
        Err(TerminalError::FailedClosed)
    );
}

#[test]
fn ground_enq_bel_xon_and_xoff_are_exact_state_noops_that_advance_the_source() {
    for (name, control) in [("ENQ", 0x05), ("BEL", 0x07), ("XON", 0x11), ("XOFF", 0x13)] {
        let mut actual = Terminal::new(4, 2).expect("terminal");
        output(&mut actual, 0, &[b'A', control, b'B']).expect(name);

        let mut expected = Terminal::new(4, 2).expect("terminal");
        output(&mut expected, 0, b"AB").expect("control-free output");

        assert_eq!(actual, expected, "{name} changed mirror terminal state");
        assert_eq!(actual.next_source_sequence(), 1, "{name}");
        assert!(!actual.is_failed_closed(), "{name}");
        assert_snapshot_self_closure(&actual);
    }
}

#[test]
fn vertical_tab_and_form_feed_match_lf_with_and_without_newline_mode() {
    for (name, control) in [("VT", 0x0b), ("FF", 0x0c)] {
        for newline_mode in [false, true] {
            let prefix: &[u8] = if newline_mode { b"\x1b[20hAB" } else { b"AB" };
            let mut actual_bytes = prefix.to_vec();
            actual_bytes.extend_from_slice(&[control, b'C']);
            let mut lf_bytes = prefix.to_vec();
            lf_bytes.extend_from_slice(b"\nC");

            let mut actual = Terminal::new(5, 3).expect("terminal");
            output(&mut actual, 0, &actual_bytes).expect(name);
            let mut expected = Terminal::new(5, 3).expect("terminal");
            output(&mut expected, 0, &lf_bytes).expect("LF reference");

            assert_eq!(actual, expected, "{name}, LNM={newline_mode}");
            assert_eq!(row_text(&actual, 0), "AB   ", "{name}");
            if newline_mode {
                assert_eq!(row_text(&actual, 1), "C    ", "{name}");
                assert_eq!(actual.cursor(), (1, 1), "{name}");
            } else {
                assert_eq!(row_text(&actual, 1), "  C  ", "{name}");
                assert_eq!(actual.cursor(), (1, 3), "{name}");
            }
            assert_eq!(actual.next_source_sequence(), 1, "{name}");
            assert_snapshot_self_closure(&actual);
        }
    }
}

#[test]
fn vertical_tab_and_form_feed_scroll_only_the_active_margins_like_lf() {
    let setup =
        b"\x1b[1;1HAAAA\x1b[2;1HBBBB\x1b[3;1HCCCC\x1b[4;1HDDDD\x1b[5;1HEEEE\x1b[2;4r\x1b[4;2H";
    for (name, control) in [("VT", 0x0b), ("FF", 0x0c)] {
        let mut actual_bytes = setup.to_vec();
        actual_bytes.push(control);
        let mut lf_bytes = setup.to_vec();
        lf_bytes.push(b'\n');

        let mut actual = Terminal::new(4, 5).expect("terminal");
        output(&mut actual, 0, &actual_bytes).expect(name);
        let mut expected = Terminal::new(4, 5).expect("terminal");
        output(&mut expected, 0, &lf_bytes).expect("LF reference");

        assert_eq!(actual, expected, "{name} margin behavior");
        assert_eq!(row_text(&actual, 0), "AAAA", "{name}");
        assert_eq!(row_text(&actual, 1), "CCCC", "{name}");
        assert_eq!(row_text(&actual, 2), "DDDD", "{name}");
        assert_eq!(row_text(&actual, 3), "    ", "{name}");
        assert_eq!(row_text(&actual, 4), "EEEE", "{name}");
        assert_eq!(actual.cursor(), (3, 1), "{name}");
        assert_eq!(actual.next_source_sequence(), 1, "{name}");
        assert_snapshot_self_closure(&actual);
    }
}

#[test]
fn accepted_ground_controls_do_not_mask_unknown_c0_and_failure_is_atomic() {
    let mut terminal = Terminal::new(8, 3).expect("terminal");
    output(&mut terminal, 0, b"stable").expect("baseline");
    let before = terminal.clone();

    assert_eq!(
        output(
            &mut terminal,
            1,
            &[0x05, 0x07, 0x0b, 0x0c, 0x11, 0x13, b'X', 0x01],
        ),
        Err(TerminalError::Unsupported("C0 control"))
    );

    let mut expected = before;
    expected.failed_closed = true;
    assert_eq!(terminal, expected);
    assert_eq!(terminal.next_source_sequence(), 1);
    assert_eq!(row_text(&terminal, 0), "stable  ");
}

#[test]
fn osc_bel_completes_the_string_instead_of_becoming_a_ground_bell() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b]2;BEL title\x07X").expect("BEL-terminated OSC");

    assert_eq!(terminal.osc_state().window_title(), "BEL title");
    assert_eq!(row_text(&terminal, 0), "X       ");
    assert_eq!(terminal.cursor(), (0, 1));
    assert_eq!(terminal.next_source_sequence(), 1);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn cjk_emoji_and_combining_form_valid_grapheme_cells() {
    let mut terminal = Terminal::new(12, 3).expect("terminal");
    output(&mut terminal, 0, "Ae\u{301}界😀Z".as_bytes()).expect("Unicode output");

    let latin = terminal.cell(0, 1).expect("combining lead");
    assert_eq!(latin.text(), "e\u{301}");
    assert_eq!(latin.width, 1);
    let cjk = terminal.cell(0, 2).expect("CJK lead");
    assert_eq!(cjk.text(), "界");
    assert_eq!(cjk.width, 2);
    assert!(terminal.cell(0, 3).expect("CJK continuation").continuation);
    let emoji = terminal.cell(0, 4).expect("emoji lead");
    assert_eq!(emoji.width, 2);
    assert!(
        terminal
            .cell(0, 5)
            .expect("emoji continuation")
            .continuation
    );
    assert_eq!(terminal.cell(0, 6).expect("suffix").character, 'Z');
    assert_snapshot_self_closure(&terminal);

    let mut joined = Terminal::new(8, 2).expect("terminal");
    output(&mut joined, 0, "👩\u{200d}💻X".as_bytes()).expect("joined emoji grapheme");
    assert_eq!(
        joined.cell(0, 0).expect("joined lead").text(),
        "👩\u{200d}💻"
    );
    assert_eq!(joined.cell(0, 0).expect("joined lead").width, 2);
    assert!(joined.cell(0, 1).expect("joined continuation").continuation);
    assert_eq!(joined.cell(0, 2).expect("joined suffix").character, 'X');
    assert_snapshot_self_closure(&joined);
}

#[test]
fn orphan_continuation_is_neither_exportable_nor_importable() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    terminal.main.cells[0] = Cell::continuation(Attributes::default());
    assert_eq!(
        terminal.export_snapshot_v2(),
        Err(TerminalError::MalformedSnapshot)
    );
}

#[test]
fn wide_erase_and_primary_logical_line_reflow_never_orphan_continuations() {
    let mut terminal = Terminal::new(6, 4).expect("terminal");
    output(&mut terminal, 0, "ab界cdEF".as_bytes()).expect("wrapped wide output");
    assert_eq!(row_text(&terminal, 0), "ab界·cd");
    assert_eq!(row_text(&terminal, 1), "EF    ");

    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 4,
            rows: 4,
        })
        .expect("logical reflow");
    assert_eq!(row_text(&terminal, 0), "ab界·");
    assert_eq!(row_text(&terminal, 1), "cdEF");
    assert_snapshot_self_closure(&terminal);

    output(&mut terminal, 2, b"\x1b[1;4H\x1b[K").expect("erase from continuation");
    assert_eq!(row_text(&terminal, 0), "ab  ");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn primary_reflow_bottom_anchors_overflow_before_the_next_mutation() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    output(&mut terminal, 0, b"abcdefgh").expect("fill two wrapped rows");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 2,
            rows: 2,
        })
        .expect("reflow and bottom-anchor");
    assert_eq!(row_text(&terminal, 0), "ef");
    assert_eq!(row_text(&terminal, 1), "gh");
    assert_eq!(terminal.main.stored_rows(), 2);
    assert_snapshot_self_closure(&terminal);

    output(&mut terminal, 2, b"X").expect("suffix after anchored resize");
    assert_eq!(row_text(&terminal, 0), "gh");
    assert_eq!(row_text(&terminal, 1), "X ");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn primary_reflow_keeps_an_explicit_cursor_when_bottom_content_must_be_clipped() {
    let mut terminal = Terminal::new(4, 3).expect("terminal");
    output(&mut terminal, 0, b"AAAA\r\nBBBB\r\nCCCC\x1b[1;1H")
        .expect("content with cursor returned to top");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 4,
            rows: 2,
        })
        .expect("cursor-anchored vertical shrink");
    assert_eq!(terminal.cursor(), (0, 0));
    assert_eq!(row_text(&terminal, 0), "AAAA");
    assert_eq!(row_text(&terminal, 1), "BBBB");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn reflow_distinguishes_wide_wrap_padding_from_a_typed_space() {
    let mut padded = Terminal::new(4, 2).expect("terminal");
    output(&mut padded, 0, "abc界".as_bytes()).expect("wide boundary wrap");
    assert_eq!(padded.main.meaningful_end, vec![3, 2]);
    padded
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 6,
            rows: 2,
        })
        .expect("padding-free reflow");
    assert_eq!(row_text(&padded, 0), "abc界· ");
    assert_eq!(padded.main.meaningful_end, vec![5, 0]);
    assert_snapshot_self_closure(&padded);

    let mut typed_space = Terminal::new(4, 2).expect("terminal");
    output(&mut typed_space, 0, "abc 界".as_bytes()).expect("typed space then wide");
    assert_eq!(typed_space.main.meaningful_end, vec![4, 2]);
    typed_space
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 6,
            rows: 2,
        })
        .expect("space-preserving reflow");
    assert_eq!(row_text(&typed_space, 0), "abc 界·");
    assert_eq!(typed_space.main.meaningful_end, vec![6, 0]);
    assert_snapshot_self_closure(&typed_space);
}

#[test]
fn validator_rejects_zero_width_leads_hidden_saved_cursor_and_open_tail_wrap() {
    let mut zero_width = Terminal::new(4, 2).expect("terminal");
    zero_width.main.cells[0] = Cell::lead('\u{301}', 1, Attributes::default());
    zero_width.main.meaningful_end[0] = 1;
    assert_eq!(
        zero_width.export_snapshot_v2(),
        Err(TerminalError::MalformedSnapshot)
    );

    let mut hidden_saved = Terminal::new(4, 2).expect("terminal");
    hidden_saved.main.saved.row = 2;
    assert_eq!(
        hidden_saved.export_snapshot_v2(),
        Err(TerminalError::MalformedSnapshot)
    );

    let mut open_tail = Terminal::new(4, 2).expect("terminal");
    open_tail.main.soft_wrapped[1] = true;
    open_tail.main.meaningful_end[1] = 1;
    assert_eq!(
        open_tail.export_snapshot_v2(),
        Err(TerminalError::MalformedSnapshot)
    );
}

#[test]
fn alternate_same_size_resize_is_noop_and_width_change_is_crop_pad() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b[?1049habcd").expect("wrapped alternate row");
    assert!(terminal.alternate.wrap_pending);
    let before = terminal.alternate.clone();
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 4,
            rows: 2,
        })
        .expect("same-size resize event");
    assert_eq!(terminal.alternate, before);
    output(&mut terminal, 2, b"X").expect("wrap survives no-op resize");
    assert_eq!(row_text(&terminal, 0), "abcd");
    assert_eq!(row_text(&terminal, 1), "X   ");
    assert!(terminal.alternate.soft_wrapped[0]);
    let wrapped = terminal.alternate.clone();
    terminal
        .apply_event(Event::Resize {
            source_sequence: 3,
            columns: 4,
            rows: 2,
        })
        .expect("same-size resize with established soft-wrap metadata");
    assert_eq!(terminal.alternate, wrapped);

    terminal
        .apply_event(Event::Resize {
            source_sequence: 4,
            columns: 3,
            rows: 2,
        })
        .expect("crop alternate width");
    assert_eq!(row_text(&terminal, 0), "abc");
    assert_eq!(row_text(&terminal, 1), "X  ");
    assert_eq!(terminal.alternate.soft_wrapped, vec![false, false]);
    terminal
        .apply_event(Event::Resize {
            source_sequence: 5,
            columns: 5,
            rows: 2,
        })
        .expect("pad alternate width");
    assert_eq!(row_text(&terminal, 0), "abc  ");
    assert_eq!(row_text(&terminal, 1), "X    ");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn alternate_47_preserves_content_and_1047_clears_only_when_leaving() {
    let mut terminal = Terminal::new(6, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[?47h\x1b[HOLD\x1b7\x1b[?47h\x1b[?47l\x1b[?47l",
    )
    .expect("47 enter, repeated enter, and repeated leave");
    let saved = terminal.alternate.saved;
    assert!(!terminal.is_alternate_screen_active());
    assert_eq!(terminal.alternate.cells[0].character, 'O');
    assert_eq!(terminal.alternate.cells[1].character, 'L');
    assert_eq!(terminal.alternate.cells[2].character, 'D');

    output(&mut terminal, 1, b"\x1b[?1047l").expect("inactive 1047 reset is a no-op");
    assert_eq!(terminal.alternate.saved, saved);
    assert_eq!(terminal.alternate.cells[0].character, 'O');

    output(&mut terminal, 2, b"\x1b[?1047h\x1b[?1047h")
        .expect("1047 enters without clearing and repeated set is a no-op");
    assert!(terminal.is_alternate_screen_active());
    assert_eq!(row_text(&terminal, 0), "OLD   ");
    assert_eq!(terminal.alternate.saved, saved);

    output(&mut terminal, 3, b"\x1b[?1047l").expect("1047 clears on exit");
    assert!(!terminal.is_alternate_screen_active());
    assert!(
        terminal
            .alternate
            .cells
            .iter()
            .all(|cell| *cell == Cell::default())
    );
    assert_eq!(terminal.alternate.saved, screen::SavedCursor::default());
    let cleared = terminal.alternate.clone();
    output(&mut terminal, 4, b"\x1b[?1047l").expect("repeated 1047 reset is a no-op");
    assert_eq!(terminal.alternate, cleared);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn alternate_1048_saves_cursor_and_charset_on_the_current_screen() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b(0\x1b[2;3H\x1b[?1048h")
        .expect("save main cursor and DEC charset");
    let main_saved_cursor = terminal.main.saved;
    let main_saved_charset = terminal.main_saved_charset;

    output(&mut terminal, 1, b"\x1b[?47h\x1b(B\x1b[1;5H\x1b[?1048h")
        .expect("switch and save alternate cursor and ASCII charset");
    let alternate_saved_cursor = terminal.alternate.saved;
    let alternate_saved_charset = terminal.alternate_saved_charset;
    assert_ne!(main_saved_charset, alternate_saved_charset);

    output(&mut terminal, 2, b"\x1b(0\x1b[2;1H\x1b[?1048l")
        .expect("restore alternate cursor and charset");
    assert_eq!(terminal.cursor(), (0, 4));
    assert_eq!(terminal.charset, alternate_saved_charset);
    assert_eq!(terminal.main.saved, main_saved_cursor);

    output(&mut terminal, 3, b"\x1b[?47l\x1b(B\x1b[1;1H\x1b[?1048lq")
        .expect("restore main cursor and charset");
    assert_eq!(terminal.main.saved, main_saved_cursor);
    assert_eq!(terminal.alternate.saved, alternate_saved_cursor);
    assert_eq!(terminal.charset, main_saved_charset);
    assert_eq!(
        terminal.cell(1, 2).expect("DEC-special q").character,
        '\u{2500}'
    );
    assert_eq!(terminal.cursor(), (1, 3));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn alternate_multi_parameter_modes_execute_in_wire_order() {
    fn retained_alternate() -> Terminal {
        let mut terminal = Terminal::new(6, 2).expect("terminal");
        output(&mut terminal, 0, b"\x1b[?47h\x1b[HOLD\x1b[?47l\x1b[HMAIN")
            .expect("retained alternate fixture");
        terminal
    }

    let mut preserve_on_entry = retained_alternate();
    output(&mut preserve_on_entry, 1, b"\x1b[?47;1049h")
        .expect("47 enters before already-active 1049");
    assert_eq!(row_text(&preserve_on_entry, 0), "OLD   ");

    let mut clear_on_entry = retained_alternate();
    output(&mut clear_on_entry, 1, b"\x1b[?1049;47h")
        .expect("1049 clears before already-active 47");
    assert_eq!(row_text(&clear_on_entry, 0), "      ");

    output(&mut preserve_on_entry, 2, b"\x1b[?47;1047l")
        .expect("47 leaves before inactive 1047 reset");
    assert_eq!(preserve_on_entry.alternate.cells[0].character, 'O');

    let mut clear_on_exit = retained_alternate();
    output(&mut clear_on_exit, 1, b"\x1b[?47h").expect("enter retained alternate");
    output(&mut clear_on_exit, 2, b"\x1b[?1047;47l").expect("1047 clears before inactive 47 reset");
    assert_eq!(clear_on_exit.alternate.cells[0].character, ' ');
    assert_snapshot_self_closure(&preserve_on_entry);
    assert_snapshot_self_closure(&clear_on_entry);
    assert_snapshot_self_closure(&clear_on_exit);
}

#[test]
fn alternate_modes_are_split_safe_at_every_output_boundary() {
    let bytes = b"\x1b[?47hA\x1b[?47lB\x1b[?1047hC\x1b[?1047lD\x1b(0\x1b[?1048h\x1b(B\x1b[?1048lq\x1b[?1049hE\x1b[?1049hF\x1b[?1049lG";
    let mut expected = Terminal::new(8, 2).expect("terminal");
    output(&mut expected, 0, bytes).expect("joined alternate modes");
    output(&mut expected, 1, b"").expect("matching source sequence");

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(8, 2).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("alternate mode prefix");
        output(&mut actual, 1, &bytes[split..]).expect("alternate mode suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }
    assert_snapshot_self_closure(&expected);
}

#[test]
fn alternate_multi_parameter_and_batch_failures_are_atomic() {
    let mut terminal = Terminal::new(6, 2).expect("terminal");
    output(&mut terminal, 0, b"MAIN").expect("baseline main content");
    let before = terminal.clone();
    let events = [
        Event::Output {
            source_sequence: 1,
            bytes: b"\x1b[?1049hALT",
        },
        Event::Output {
            source_sequence: 2,
            bytes: b"\x1b[?47;9999l",
        },
    ];
    assert_eq!(
        terminal.apply_events_atomic(&events),
        Err(TerminalError::Unsupported("private mode"))
    );
    let mut expected = before;
    expected.failed_closed = true;
    assert_eq!(terminal, expected);
}

#[test]
fn alternate_saved_cursors_and_charsets_survive_snapshot_suffix_replay() {
    let mut direct = Terminal::new(8, 2).expect("terminal");
    output(
        &mut direct,
        0,
        b"\x1b(0\x1b[2;3H\x1b[?1048h\x1b[?47h\x1b(B\x1b[1;5H\x1b[?1048hALT",
    )
    .expect("save both screens before checkpoint");
    let snapshot = direct.export_snapshot_v2().expect("alternate checkpoint");
    let mut restored = Terminal::import_snapshot_v2(&snapshot).expect("alternate restore");
    let suffix = b"\x1b[2;1H\x1b(0\x1b[?1048l\x1b[?1047l\x1b[?1048lq\x1b[?1049hX\x1b[?1049lY";
    output(&mut direct, 1, suffix).expect("direct alternate suffix");
    output(&mut restored, 1, suffix).expect("restored alternate suffix");

    assert_eq!(restored, direct);
    assert_eq!(
        direct.cell(1, 2).expect("restored DEC q").character,
        '\u{2500}'
    );
    assert!(
        direct
            .alternate
            .cells
            .iter()
            .all(|cell| *cell == Cell::default())
    );
    assert_snapshot_self_closure(&direct);
}

#[test]
fn active_alternate_shrink_expand_preserves_primary_logical_cursor() {
    let mut terminal = Terminal::new(10, 3).expect("terminal");
    output(&mut terminal, 0, b"123456789\x1b[?1049hALT").expect("enter alternate");
    assert!(terminal.is_alternate_screen_active());
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 4,
            rows: 2,
        })
        .expect("shrink while alternate active");
    assert_eq!(row_text(&terminal, 0), "ALT ");
    assert_snapshot_self_closure(&terminal);

    terminal
        .apply_event(Event::Resize {
            source_sequence: 2,
            columns: 10,
            rows: 3,
        })
        .expect("expand while alternate active");
    output(&mut terminal, 3, b"\x1b[?1049l!").expect("restore primary");
    assert_eq!(row_text(&terminal, 0), "123456789!");
    assert_eq!(terminal.cursor(), (0, 9));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn hard_reset_after_frozen_primary_resize_reunifies_screen_dimensions() {
    let mut terminal = Terminal::new(1, 1).expect("terminal");
    output(&mut terminal, 0, b"\x1b[?1049h").expect("enter alternate");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 4,
            rows: 2,
        })
        .expect("resize with frozen primary");
    assert_eq!((terminal.main.columns, terminal.main.rows), (1, 1));
    output(&mut terminal, 2, b"\x1bc").expect("hard reset");
    assert!(!terminal.is_alternate_screen_active());
    assert_eq!((terminal.main.columns, terminal.main.rows), (4, 2));
    assert_eq!(
        (terminal.alternate.columns, terminal.alternate.rows),
        (4, 2)
    );
    assert_eq!(terminal.cell(1, 3).expect("reset grid cell").character, ' ');
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn leaving_one_column_alternate_degrades_frozen_wide_grapheme_safely() {
    let mut terminal = Terminal::new(6, 1).expect("terminal");
    output(&mut terminal, 0, "A界B\u{1b}[?1049h".as_bytes()).expect("freeze wide primary");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 1,
            rows: 2,
        })
        .expect("one-column alternate");
    output(&mut terminal, 2, b"\x1b[?1049l").expect("safe primary reflow on exit");
    assert!(!terminal.is_failed_closed());
    assert!(!terminal.is_alternate_screen_active());
    assert_eq!(
        (row_text(&terminal, 0), row_text(&terminal, 1)),
        ("�".to_owned(), "B".to_owned())
    );
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn active_alternate_resize_to_one_column_never_reflows_frozen_wide_primary() {
    let mut terminal = Terminal::new(6, 3).expect("terminal");
    output(&mut terminal, 0, "A界B\u{1b}[?1049hALT".as_bytes()).expect("enter alternate");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 1,
            rows: 2,
        })
        .expect("active alternate resize cannot reject on frozen main width");
    assert_eq!(terminal.dimensions(), (1, 2));
    assert_snapshot_self_closure(&terminal);
    terminal
        .apply_event(Event::Resize {
            source_sequence: 2,
            columns: 6,
            rows: 3,
        })
        .expect("expand alternate");
    output(&mut terminal, 3, b"\x1b[?1049l!").expect("return to wide primary");
    assert_eq!(row_text(&terminal, 0), "A界·B! ");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn heart_zwj_fire_cluster_upgrades_to_wide_before_following_text() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, "❤️\u{200d}🔥X".as_bytes()).expect("emoji ZWJ cluster");
    let heart = terminal.cell(0, 0).expect("cluster lead");
    assert_eq!(heart.text(), "❤️\u{200d}🔥");
    assert_eq!(heart.width, 2);
    assert!(
        terminal
            .cell(0, 1)
            .expect("cluster continuation")
            .continuation
    );
    assert_eq!(terminal.cell(0, 2).expect("following text").character, 'X');
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn reflow_amplification_is_preflighted_before_layout_allocation() {
    let mut malicious = Screen::new(1, 1);
    malicious.cells = vec![Cell::default(); MAX_CELLS];
    malicious.soft_wrapped = vec![false; MAX_CELLS];
    malicious.meaningful_end = vec![1; MAX_CELLS];
    assert_eq!(
        malicious.validate_reflow_target(512),
        Err(TerminalError::LimitExceeded)
    );
    assert_eq!(malicious.validate(), Err(TerminalError::MalformedSnapshot));

    let terminal = Terminal {
        columns: 512,
        rows: 1,
        main: malicious,
        alternate: Screen::new(512, 1),
        active_alternate: true,
        cursor_visible: true,
        cursor_shape: CursorShape::Block,
        cursor_blinking: true,
        modes: ModeState::default(),
        main_keyboard_protocol: KeyboardProtocolStack::default(),
        alternate_keyboard_protocol: KeyboardProtocolStack::default(),
        margins: Margins::full(512, 1),
        tabs: TabStops::new(512),
        charset: CharsetState::default(),
        main_saved_charset: CharsetState::default(),
        alternate_saved_charset: CharsetState::default(),
        osc: OscState::default(),
        next_source_sequence: 0,
        logical_time_micros: 0,
        synchronized_output_deadline_micros: None,
        output_profile: OutputProfile::Utf8,
        parser: ParserState::Ground,
        utf8: Utf8Decoder::default(),
        string_utf8: StringUtf8Boundary::default(),
        repeat_source: None,
        failed_closed: false,
    };
    let malicious_snapshot =
        snapshot::export_unchecked_for_test(&terminal).expect("malicious snapshot fixture");
    assert_eq!(
        Terminal::import_snapshot_v2(&malicious_snapshot),
        Err(TerminalError::MalformedSnapshot)
    );
}

#[test]
fn every_ground_event_boundary_restores_with_equivalent_suffix() {
    let events: [&[u8]; 5] = [
        b"prefix",
        b"\x1b[2;3H@",
        "界e\u{301}".as_bytes(),
        b"\x1b[1;32mgreen\x1b[0m",
        b"\r\nend",
    ];
    let mut uninterrupted = Terminal::new(16, 5).expect("terminal");
    for (index, bytes) in events.iter().enumerate() {
        output(
            &mut uninterrupted,
            u64::try_from(index).expect("test index"),
            bytes,
        )
        .expect("ground-ending event");
        let snapshot = uninterrupted
            .export_snapshot_v2()
            .expect("boundary snapshot");
        let mut restored = Terminal::import_snapshot_v2(&snapshot).expect("boundary restore");
        let suffix_sequence = u64::try_from(index + 1).expect("test index");
        output(&mut restored, suffix_sequence, b"suffix").expect("restored suffix");
        let mut expected = uninterrupted.clone();
        output(&mut expected, suffix_sequence, b"suffix").expect("expected suffix");
        assert_eq!(expected, restored);
    }
}

#[test]
fn cursor_erase_sgr_and_advance_remain_deterministic() {
    let mut terminal = Terminal::new(10, 3).expect("terminal");
    output(
        &mut terminal,
        0,
        b"hello\x1b[2;3H\x1b[1;31;48;2;1;2;3mX\x1b[0mY",
    )
    .expect("VT subset");
    let styled = terminal.cell(1, 2).expect("styled cell");
    assert!(styled.attributes.bold);
    assert_eq!(styled.attributes.foreground, Color::Indexed(1));
    assert_eq!(styled.attributes.background, Color::Rgb(1, 2, 3));
    terminal
        .apply_event(Event::Advance {
            source_sequence: 1,
            micros: 42,
        })
        .expect("advance");
    assert_eq!(terminal.logical_time_micros(), 42);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn conceal_and_strikethrough_sgr_match_at_every_output_split() {
    let bytes = b"\x1b[8;9mA\x1b[28mB\x1b[29mC\x1b[8;9;0mD";
    let mut expected = Terminal::new(6, 1).expect("terminal");
    output(&mut expected, 0, bytes).expect("joined conceal and strikethrough SGR");
    output(&mut expected, 1, b"").expect("matching event count");

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(6, 1).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("SGR prefix");
        output(&mut actual, 1, &bytes[split..]).expect("SGR suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }

    let concealed_and_struck = expected.cell(0, 0).expect("styled A").attributes;
    assert!(concealed_and_struck.conceal);
    assert!(concealed_and_struck.strikethrough);
    let struck = expected.cell(0, 1).expect("visible struck B").attributes;
    assert!(!struck.conceal);
    assert!(struck.strikethrough);
    assert_eq!(
        expected.cell(0, 2).expect("plain C").attributes,
        Attributes::default()
    );
    assert_eq!(
        expected.cell(0, 3).expect("SGR 0 D").attributes,
        Attributes::default()
    );
    assert_snapshot_self_closure(&expected);
}

#[test]
fn conceal_and_strikethrough_atomic_batch_failure_rolls_back_all_renditions() {
    let mut terminal = Terminal::new(6, 1).expect("terminal");
    let before = terminal.clone();
    let events = [
        Event::Output {
            source_sequence: 0,
            bytes: b"\x1b[8;9mX",
        },
        Event::Output {
            source_sequence: 1,
            bytes: b"\x1b[28;29mY\x1b[999m",
        },
    ];

    assert_eq!(
        terminal.apply_events_atomic(&events),
        Err(TerminalError::Unsupported("SGR parameter"))
    );
    let mut expected = before;
    expected.failed_closed = true;
    assert_eq!(terminal, expected);
}

#[test]
fn conceal_and_strikethrough_survive_bce_cursor_screens_resize_and_snapshot_suffix() {
    let mut direct = Terminal::new(4, 2).expect("terminal");
    output(&mut direct, 0, b"\x1b[8;9;44mA\x1b7\x1b[X").expect("styled BCE and cursor save");

    let bce = direct.main.cells[1].attributes;
    assert!(!bce.conceal);
    assert!(!bce.strikethrough);
    assert_eq!(bce.background, Color::Indexed(4));
    output(&mut direct, 1, b"\x1b[0mB\x1b8C\x1b[?1049hD")
        .expect("restore styled cursor and enter alternate screen");
    assert!(direct.alternate.cells[0].attributes.conceal);
    assert!(direct.alternate.cells[0].attributes.strikethrough);

    let checkpoint = direct.export_snapshot_v2().expect("styled checkpoint");
    let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("styled restore");
    for terminal in [&mut direct, &mut restored] {
        terminal
            .apply_event(Event::Resize {
                source_sequence: 2,
                columns: 5,
                rows: 3,
            })
            .expect("resize styled alternate");
        output(terminal, 3, b"E\x1b[?1049l\x1b8F\x1b[0mG")
            .expect("restore styled primary cursor and reset attributes");
    }

    assert_eq!(restored, direct);
    assert!(
        direct
            .alternate
            .cells
            .iter()
            .all(|cell| *cell == Cell::default())
    );
    let restored_cursor = direct.cell(0, 2).expect("restored F").attributes;
    assert!(restored_cursor.conceal && restored_cursor.strikethrough);
    assert_eq!(
        direct.cell(0, 3).expect("reset G").attributes,
        Attributes::default()
    );
    assert_snapshot_self_closure(&direct);
}

#[test]
fn snapshot_v2_rendition_reserved_flags_encode_new_attributes_strictly() {
    let mut terminal = Terminal::new(2, 1).expect("terminal");
    output(&mut terminal, 0, b"\x1b[4;8;9mX").expect("styled cell");
    let snapshot = terminal.export_snapshot_v2().expect("snapshot");
    let section = snapshot::section_payload_offset_for_test(&snapshot, 7);

    assert_eq!(
        u32::from_le_bytes(snapshot[section..section + 4].try_into().expect("count")),
        2
    );
    assert_eq!(
        u32::from_le_bytes(
            snapshot[section + 4..section + 8]
                .try_into()
                .expect("record size")
        ),
        48
    );
    let default_record = section + 8;
    let styled_record = default_record + 48;
    assert_eq!(&snapshot[default_record..default_record + 48], &[0; 48]);
    assert_eq!(
        u32::from_le_bytes(
            snapshot[styled_record..styled_record + 4]
                .try_into()
                .expect("rendition flags")
        ),
        0x30
    );
    assert_eq!(snapshot[styled_record + 4], 1);

    assert_snapshot_mutation_is_malformed(&snapshot, "unknown rendition reserved flag", |bytes| {
        bytes[styled_record] |= 0x40
    });
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn colon_sgr_colors_and_suffixes_match_at_every_output_split() {
    let bytes = b"\x1b[38:5:123mF\x1b[48:5:45mB\x1b[38:2::1:2:3mR\x1b[48:2:0:4:5:6;1mS";
    let mut expected = Terminal::new(8, 2).expect("terminal");
    output(&mut expected, 0, bytes).expect("joined colon SGR");
    output(&mut expected, 1, b"").expect("matching event count");

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(8, 2).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("colon SGR prefix");
        output(&mut actual, 1, &bytes[split..]).expect("colon SGR suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }

    let foreground_indexed = expected.cell(0, 0).expect("indexed foreground");
    assert_eq!(foreground_indexed.character, 'F');
    assert_eq!(
        foreground_indexed.attributes.foreground,
        Color::Indexed(123)
    );
    assert_eq!(foreground_indexed.attributes.background, Color::Default);

    let background_indexed = expected.cell(0, 1).expect("indexed background");
    assert_eq!(background_indexed.character, 'B');
    assert_eq!(
        background_indexed.attributes.foreground,
        Color::Indexed(123)
    );
    assert_eq!(background_indexed.attributes.background, Color::Indexed(45));

    let foreground_rgb = expected.cell(0, 2).expect("RGB foreground");
    assert_eq!(foreground_rgb.character, 'R');
    assert_eq!(foreground_rgb.attributes.foreground, Color::Rgb(1, 2, 3));
    assert_eq!(foreground_rgb.attributes.background, Color::Indexed(45));

    let background_rgb = expected.cell(0, 3).expect("RGB background");
    assert_eq!(background_rgb.character, 'S');
    assert_eq!(background_rgb.attributes.foreground, Color::Rgb(1, 2, 3));
    assert_eq!(background_rgb.attributes.background, Color::Rgb(4, 5, 6));
    assert!(background_rgb.attributes.bold);
    assert_snapshot_self_closure(&expected);
}

#[test]
fn extended_sgr_renditions_colors_resets_and_order_are_exact() {
    let bytes = b"\x1b[1;2;3;4:3;6;7;8;9;53m\
        \x1b[38:5:10;48:2::11:12:13;58;2;14;15;16mA\
        \x1b[22;23;4:5;25;27;28;29;55;39;49;59mB\
        \x1b[21mC\x1b[24mD\x1b[4:0mE\x1b[0mF";
    let mut expected = Terminal::new(8, 1).expect("terminal");
    output(&mut expected, 0, bytes).expect("joined extended SGR");
    output(&mut expected, 1, b"").expect("matching event count");

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(8, 1).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("extended SGR prefix");
        output(&mut actual, 1, &bytes[split..]).expect("extended SGR suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }

    let rich = expected.cell(0, 0).expect("rich A").attributes;
    assert!(rich.bold && rich.faint && rich.italic);
    assert_eq!(rich.underline_style, UnderlineStyle::Curly);
    assert!(rich.blink && rich.inverse && rich.conceal && rich.strikethrough && rich.overline);
    assert_eq!(rich.foreground, Color::Indexed(10));
    assert_eq!(rich.background, Color::Rgb(11, 12, 13));
    assert_eq!(rich.underline_color, Color::Rgb(14, 15, 16));

    let selectively_cleared = expected
        .cell(0, 1)
        .expect("selectively cleared B")
        .attributes;
    assert!(!selectively_cleared.bold && !selectively_cleared.faint);
    assert!(!selectively_cleared.italic && !selectively_cleared.blink);
    assert!(!selectively_cleared.inverse && !selectively_cleared.conceal);
    assert!(!selectively_cleared.strikethrough && !selectively_cleared.overline);
    assert_eq!(selectively_cleared.underline_style, UnderlineStyle::Dashed);
    assert_eq!(selectively_cleared.foreground, Color::Default);
    assert_eq!(selectively_cleared.background, Color::Default);
    assert_eq!(selectively_cleared.underline_color, Color::Default);

    assert_eq!(
        expected
            .cell(0, 2)
            .expect("double C")
            .attributes
            .underline_style,
        UnderlineStyle::Double
    );
    for column in 3..=5 {
        assert_eq!(
            expected
                .cell(0, column)
                .expect("reset underline cell")
                .attributes,
            Attributes::default()
        );
    }
    assert_snapshot_self_closure(&expected);
}

#[test]
fn malformed_extended_sgr_is_event_and_batch_atomic() {
    let malformed = [
        b"\x1b[4:m".as_slice(),
        b"\x1b[4:6m".as_slice(),
        b"\x1b[4::1m".as_slice(),
        b"\x1b[59:0m".as_slice(),
        b"\x1b[58;5;m".as_slice(),
        b"\x1b[58;2;1;2;m".as_slice(),
        b"\x1b[58:5:256m".as_slice(),
        b"\x1b[58:2:1:2:3:4m".as_slice(),
        b"\x1b[58;5:1m".as_slice(),
        b"\x1b[58:5;1m".as_slice(),
    ];
    for bytes in malformed {
        let mut terminal = Terminal::new(4, 1).expect("terminal");
        output(&mut terminal, 0, b"\x1b[1;2;4:4;53;58:5:9mA").expect("baseline");
        let mut expected = terminal.clone();
        expected.failed_closed = true;
        assert!(matches!(
            output(&mut terminal, 1, bytes),
            Err(TerminalError::Unsupported(_))
        ));
        assert_eq!(terminal, expected, "malformed extended SGR: {bytes:?}");
    }

    let mut batch = Terminal::new(4, 1).expect("terminal");
    let before = batch.clone();
    assert!(matches!(
        batch.apply_events_atomic(&[
            Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[2;4:2;58:5:7mX",
            },
            Event::Output {
                source_sequence: 1,
                bytes: b"\x1b[53mY\x1b[4:6m",
            },
        ]),
        Err(TerminalError::Unsupported(_))
    ));
    let mut expected = before;
    expected.failed_closed = true;
    assert_eq!(batch, expected);
}

#[test]
fn semicolon_and_colon_extended_colors_coexist_without_reinterpretation() {
    let mut terminal = Terminal::new(6, 1).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[38;5;200mI\x1b[48;2;7;8;9mR\x1b[38:5:10;48;5;11;4mM",
    )
    .expect("legacy and colon extended colors");

    assert_eq!(
        terminal
            .cell(0, 0)
            .expect("legacy indexed")
            .attributes
            .foreground,
        Color::Indexed(200)
    );
    assert_eq!(
        terminal
            .cell(0, 1)
            .expect("legacy RGB")
            .attributes
            .background,
        Color::Rgb(7, 8, 9)
    );
    let mixed = terminal.cell(0, 2).expect("mixed color syntax");
    assert_eq!(mixed.attributes.foreground, Color::Indexed(10));
    assert_eq!(mixed.attributes.background, Color::Indexed(11));
    assert!(mixed.attributes.is_underlined());
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn colon_sgr_snapshot_replays_an_equivalent_suffix() {
    let mut direct = Terminal::new(8, 2).expect("terminal");
    output(&mut direct, 0, b"\x1b[38:2::12:34:56;48:5:78mA").expect("colon SGR checkpoint");
    let checkpoint = direct.export_snapshot_v2().expect("checkpoint snapshot");
    let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("restore checkpoint");

    let suffix = b"\x1b[48:2:0:90:91:92;1mB\x1b[0mC";
    output(&mut direct, 1, suffix).expect("direct suffix");
    output(&mut restored, 1, suffix).expect("restored suffix");
    assert_eq!(restored, direct);
    assert_eq!(
        restored.export_snapshot_v2().expect("restored snapshot"),
        direct.export_snapshot_v2().expect("direct snapshot")
    );
}

#[test]
fn malformed_colon_sgr_groups_fail_closed_with_atomic_rollback() {
    let cases = [
        b"\x1b[1;38:5;42mX".to_vec(),
        b"\x1b[38;5:42mX".to_vec(),
        b"\x1b[38;5;42:1mX".to_vec(),
        b"\x1b[38:5:mX".to_vec(),
        b"\x1b[38:2::1:2:mX".to_vec(),
        b"\x1b[38:5:42:7mX".to_vec(),
        b"\x1b[38:2:1:1:2:3mX".to_vec(),
        b"\x1b[48:2::0:256:0mX".to_vec(),
        b"\x1b[38:5:65536mX".to_vec(),
    ];

    for bytes in cases {
        let mut terminal = Terminal::new(6, 1).expect("terminal");
        output(&mut terminal, 0, b"\x1b[34;4mA").expect("baseline state");
        let mut expected = terminal.clone();
        expected.failed_closed = true;

        assert!(
            matches!(
                output(&mut terminal, 1, &bytes),
                Err(TerminalError::Unsupported(_))
            ),
            "malformed input unexpectedly accepted: {bytes:?}"
        );
        assert_eq!(terminal, expected, "malformed input: {bytes:?}");
        assert_eq!(terminal.next_source_sequence(), 1);
        assert_eq!(
            terminal.cell(0, 1).expect("rolled-back suffix").character,
            ' '
        );
    }
}

#[test]
fn colon_subparameters_remain_rejected_outside_plain_sgr() {
    for bytes in [
        &b"\x1b[1:2H"[..],
        &b"\x1b[1:2$z"[..],
        &b"\x1b[?25:1h"[..],
        &b"\x1bP1:2qdata\x1b\\"[..],
    ] {
        let mut terminal = Terminal::new(6, 2).expect("terminal");
        assert!(matches!(
            output(&mut terminal, 0, bytes),
            Err(TerminalError::Unsupported(_))
        ));
        assert!(terminal.is_failed_closed());
        assert_eq!(terminal.next_source_sequence(), 0);
        assert_eq!(row_text(&terminal, 0), "      ");
    }
}

#[test]
fn conpty_utf8_selection_marker_is_an_exact_no_op_not_a_blanket_ignore() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"A\x1b[0%mB").expect("fixed UTF-8 selector");
    assert_eq!(row_text(&terminal, 0), "AB      ");

    for bytes in [b"\x1b[1%m".as_slice(), b"\x1b[0;0%m".as_slice()] {
        let mut invalid = Terminal::new(8, 2).expect("terminal");
        assert_eq!(
            output(&mut invalid, 0, bytes),
            Err(TerminalError::Unsupported("CSI intermediates"))
        );
        assert!(invalid.is_failed_closed());
    }
}

#[test]
fn csi_character_and_region_edits_are_wide_safe_and_margin_scoped() {
    let mut edited = Terminal::new(8, 2).expect("terminal");
    output(&mut edited, 0, "AB界CD\u{1b}[1;3H\u{1b}[2@".as_bytes()).expect("ICH");
    assert_eq!(row_text(&edited, 0), "AB  界·CD");
    output(&mut edited, 1, b"\x1b[1;5H\x1b[P").expect("DCH");
    assert_eq!(row_text(&edited, 0), "AB   CD ");
    output(&mut edited, 2, b"\x1b[1;2H\x1b[2X").expect("ECH");
    assert_eq!(row_text(&edited, 0), "A    CD ");
    assert_snapshot_self_closure(&edited);

    let mut scrolled = Terminal::new(4, 4).expect("terminal");
    output(
        &mut scrolled,
        0,
        b"\x1b[1;1HAAAA\x1b[2;1HBBBB\x1b[3;1HCCCC\x1b[4;1HDDDD\x1b[2;4r\x1b[2;1H\x1b[S",
    )
    .expect("margin scroll");
    assert_eq!(row_text(&scrolled, 0), "AAAA");
    assert_eq!(row_text(&scrolled, 1), "CCCC");
    assert_eq!(row_text(&scrolled, 2), "DDDD");
    assert_eq!(row_text(&scrolled, 3), "    ");
    assert_eq!(scrolled.cursor(), (1, 0));
    assert_snapshot_self_closure(&scrolled);
}

#[test]
fn insert_autowrap_origin_and_saved_modes_follow_terminal_semantics() {
    let mut overwrite = Terminal::new(4, 2).expect("terminal");
    output(&mut overwrite, 0, b"abcd\x1b[?7lX\x1b[?7hYZ").expect("DECAWM");
    assert_eq!(row_text(&overwrite, 0), "abcY");
    assert_eq!(row_text(&overwrite, 1), "Z   ");

    let mut insert = Terminal::new(4, 2).expect("terminal");
    output(&mut insert, 0, b"abcd\x1b[1;2H\x1b[4hXY").expect("IRM");
    assert_eq!(row_text(&insert, 0), "aXYb");

    let mut origin = Terminal::new(5, 5).expect("terminal");
    output(
        &mut origin,
        0,
        b"\x1b[2;4r\x1b[?6h\x1b[HX\x1b7\x1b[?6l\x1b[?7l\x1b8Y",
    )
    .expect("DECOM and DECSC");
    assert_eq!(origin.cell(1, 0).expect("origin home").character, 'X');
    assert_eq!(origin.cell(1, 1).expect("restored suffix").character, 'Y');
    assert!(origin.modes.origin_mode);
    assert!(origin.modes.auto_wrap);
    assert_snapshot_self_closure(&origin);
}

#[test]
fn dynamic_tabs_and_input_modes_survive_snapshot_suffix_replay() {
    let mut uninterrupted = Terminal::new(20, 3).expect("terminal");
    output(
        &mut uninterrupted,
        0,
        b"\x1b[3g\x1b[1;4H\x1bH\r\tX\x1b[4h\x1b[?1h\x1b[?1004h\x1b[?1006h\x1b[?2004h\x1b[2;3r",
    )
    .expect("tabs and modes");
    assert_eq!(uninterrupted.cell(0, 3).expect("custom tab").character, 'X');
    let checkpoint = uninterrupted.export_snapshot_v2().expect("checkpoint");
    let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("restore");
    assert_eq!(restored.modes, uninterrupted.modes);
    assert_eq!(restored.margins, uninterrupted.margins);
    assert_eq!(restored.tabs, uninterrupted.tabs);

    let suffix = b"\x1b[1;1HAB\x1b[1DZ";
    output(&mut uninterrupted, 1, suffix).expect("direct suffix");
    output(&mut restored, 1, suffix).expect("restored suffix");
    assert_eq!(restored, uninterrupted);
    assert_eq!(
        restored.export_snapshot_v2().expect("restored final"),
        uninterrupted.export_snapshot_v2().expect("direct final")
    );
}

#[test]
fn charset_designation_invocation_single_shift_and_saved_cursor_are_recoverable() {
    let mut uninterrupted = Terminal::new(16, 3).expect("terminal");
    output(
        &mut uninterrupted,
        0,
        b"\x1b)0\x0eq\x0f-\x1b*0\x1bNq\x1b7\x1b(B\x1b8q",
    )
    .expect("charset controls");
    assert_eq!(
        row_text(&uninterrupted, 0),
        "\u{2500}-\u{2500}q            "
    );

    output(&mut uninterrupted, 1, b"\x1b*0\x1bN").expect("pending single shift");
    let checkpoint = uninterrupted.export_snapshot_v2().expect("checkpoint");
    let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("restore");
    assert_eq!(restored.charset, uninterrupted.charset);
    assert_eq!(
        restored.main_saved_charset,
        uninterrupted.main_saved_charset
    );

    output(&mut uninterrupted, 2, b"qX").expect("direct suffix");
    output(&mut restored, 2, b"qX").expect("restored suffix");
    assert_eq!(restored, uninterrupted);
    assert_eq!(
        row_text(&restored, 0),
        "\u{2500}-\u{2500}q\u{2500}X          "
    );
    assert_snapshot_self_closure(&restored);
}

#[test]
fn a_fresh_alternate_screen_does_not_restore_a_stale_saved_charset() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[?1049h\x1b(0\x1b7\x1b[?1049l\x1b[?1049h\x1b(0\x1b8q",
    )
    .expect("re-enter alternate and restore its reset cursor state");

    assert_eq!(row_text(&terminal, 0), "q       ");
    assert_eq!(terminal.charset, CharsetState::default());
    assert_eq!(terminal.alternate_saved_charset, CharsetState::default());
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn unknown_charset_designation_fails_closed_atomically() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut terminal, 0, b"prefix\x1b(?"),
        Err(TerminalError::Unsupported("charset designation"))
    );
    assert!(terminal.is_failed_closed());
    assert_eq!(row_text(&terminal, 0), "        ");
}

#[test]
fn decnrcm_gates_designated_nrc_without_discarding_the_designation() {
    let mut terminal = Terminal::new(12, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b(A#\x1b[?42h#\x1b[?42l#\x1b[?42h#")
        .expect("British NRC with mode transitions");

    assert_eq!(row_text(&terminal, 0), "#\u{00a3}#\u{00a3}        ");
    assert!(terminal.modes.national_replacement_character_set_mode);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn portuguese_nrc_designation_is_deterministic_at_every_output_split() {
    let bytes = b"\x1b(%6\x1b[?42h[\\]{|}";
    for split in 0..=bytes.len() {
        let mut expected = Terminal::new(12, 2).expect("expected terminal");
        output(&mut expected, 0, b"").expect("empty first source event");
        output(&mut expected, 1, bytes).expect("unsplit Portuguese NRC");

        let mut actual = Terminal::new(12, 2).expect("actual terminal");
        output(&mut actual, 0, &bytes[..split]).expect("NRC prefix");
        output(&mut actual, 1, &bytes[split..]).expect("NRC suffix");

        assert_eq!(actual, expected, "split at byte {split}");
        assert_eq!(
            row_text(&actual, 0),
            "\u{00c3}\u{00c7}\u{00d5}\u{00e3}\u{00e7}\u{00f5}      "
        );
        assert_snapshot_self_closure(&actual);
    }
}

#[test]
fn nrc_runtime_respects_locking_shift_utf8_and_mode_aware_decfra() {
    let mut terminal = Terminal::new(10, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        "\u{1b})K\u{000e}\u{1b}[?42h[\\]{|}~\u{00c4}".as_bytes(),
    )
    .expect("German NRC through G1 and UTF-8");
    assert_eq!(
        row_text(&terminal, 0),
        "\u{00c4}\u{00d6}\u{00dc}\u{00e4}\u{00f6}\u{00fc}\u{00df}\u{00c4}  "
    );

    output(&mut terminal, 1, b"\x1b(A\x0f\x1b[35;2;1;2;3$x").expect("mode-aware DECFRA");
    assert_eq!(row_text(&terminal, 1), "\u{00a3}\u{00a3}\u{00a3}       ");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn nrc_saved_charset_snapshot_replays_the_same_suffix() {
    let mut direct = Terminal::new(8, 2).expect("terminal");
    output(&mut direct, 0, b"\x1b(A\x1b[?42h#\x1b7")
        .expect("designate, enable, print, and save NRC");
    let checkpoint = direct.export_snapshot_v2().expect("NRC snapshot");
    let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("restore NRC snapshot");

    let suffix = b"\x1b(B#\x1b8#";
    output(&mut direct, 1, suffix).expect("direct suffix");
    output(&mut restored, 1, suffix).expect("restored suffix");

    assert_eq!(restored, direct);
    assert_eq!(row_text(&direct, 0), "\u{00a3}\u{00a3}      ");
    assert_eq!(
        restored.export_snapshot_v2().expect("restored export"),
        direct.export_snapshot_v2().expect("direct export")
    );
}

#[test]
fn malformed_prefixed_nrc_designation_is_atomic_and_ris_clears_nrc_state() {
    let mut malformed = Terminal::new(8, 2).expect("terminal");
    assert_eq!(
        output(&mut malformed, 0, b"prefix\x1b(%B"),
        Err(TerminalError::Unsupported("charset designation"))
    );
    assert!(malformed.is_failed_closed());
    assert_eq!(row_text(&malformed, 0), "        ");

    let mut reset = Terminal::new(8, 2).expect("terminal");
    output(&mut reset, 0, b"\x1b(A\x1b[?42h#\x1bc#").expect("NRC followed by RIS");
    assert_eq!(row_text(&reset, 0), "#       ");
    assert!(!reset.modes.national_replacement_character_set_mode);
    assert_eq!(reset.charset, CharsetState::default());
    assert_snapshot_self_closure(&reset);
}

#[test]
fn default_full_margins_expand_with_the_terminal() {
    let mut terminal = Terminal::new(3, 2).expect("terminal");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 0,
            columns: 3,
            rows: 3,
        })
        .expect("expand terminal");
    output(&mut terminal, 1, b"abcdefghi").expect("fill expanded terminal");

    assert_eq!(row_text(&terminal, 0), "abc");
    assert_eq!(row_text(&terminal, 1), "def");
    assert_eq!(row_text(&terminal, 2), "ghi");
    assert_eq!(terminal.margins, Margins::full(3, 3));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn decstbm_rejects_equal_wire_parameters_without_moving_the_cursor() {
    let mut terminal = Terminal::new(6, 5).expect("terminal");
    output(&mut terminal, 0, b"\x1b[2;4r\x1b[5;3H").expect("set valid margins and cursor");
    let margins = terminal.margins;
    let cursor = terminal.cursor();

    output(&mut terminal, 1, b"\x1b[2;2r").expect("invalid equal DECSTBM is ignored");

    assert_eq!(terminal.margins, margins);
    assert_eq!(terminal.cursor(), cursor);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn declrmm_decslrm_and_ansi_save_cursor_have_unambiguous_state_transitions() {
    let mut terminal = Terminal::new(8, 4).expect("terminal");
    output(&mut terminal, 0, b"\x1b[3;6H\x1b[s\x1b[1;1H\x1b[u")
        .expect("ANSI save and restore while DECLRMM is disabled");
    assert_eq!(terminal.cursor(), (2, 5));

    output(&mut terminal, 1, b"\x1b[2;4r\x1b[?69h\x1b[2;7s")
        .expect("enable DECLRMM and set horizontal margins");
    assert!(terminal.modes.left_right_margin);
    assert_eq!(terminal.margins.top, 1);
    assert_eq!(terminal.margins.bottom, 4);
    assert_eq!(terminal.margins.left, 1);
    assert_eq!(terminal.margins.right, 7);
    assert_eq!(terminal.cursor(), (0, 0));

    output(&mut terminal, 2, b"\x1b[?6h\x1b[3;4H").expect("origin-relative cursor addressing");
    assert_eq!(terminal.cursor(), (3, 4));
    let margins = terminal.margins;
    let cursor = terminal.cursor();
    output(&mut terminal, 3, b"\x1b[7;2s").expect("invalid DECSLRM is a state no-op");
    assert_eq!(terminal.margins, margins);
    assert_eq!(terminal.cursor(), cursor);

    output(&mut terminal, 4, b"\x1b[s").expect("default DECSLRM");
    assert_eq!(terminal.margins.left, 0);
    assert_eq!(terminal.margins.right, 8);
    assert!(terminal.margins.horizontal_follows_screen);
    assert_eq!(terminal.cursor(), (1, 0));

    output(&mut terminal, 5, b"\x1b[2;6s\x1b[?69h")
        .expect("repeated DECSET resets horizontal margins");
    assert!(terminal.modes.left_right_margin);
    assert_eq!(terminal.margins.left, 0);
    assert_eq!(terminal.margins.right, 8);
    output(&mut terminal, 6, b"\x1b[2;6s\x1b[?69l")
        .expect("DECRST resets and disables horizontal margins");
    assert!(!terminal.modes.left_right_margin);
    assert_eq!(terminal.margins.left, 0);
    assert_eq!(terminal.margins.right, 8);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn declrmm_snapshot_roundtrip_replays_suffix_frames_and_cpr_effects() {
    let mut direct = Terminal::new(8, 4).expect("terminal");
    output(
        &mut direct,
        0,
        b"\x1b[2;4r\x1b[?69h\x1b[2;7s\x1b[?6h\x1b[2;3HAB",
    )
    .expect("establish DECLRMM state");
    let checkpoint = direct.export_snapshot_v2().expect("DECLRMM snapshot");
    let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("restore DECLRMM snapshot");
    assert_eq!(restored, direct);
    assert_eq!(
        restored.export_snapshot_v2().expect("canonical re-export"),
        checkpoint
    );

    let suffix = b"\x1b[1;1HX\r\tY\x1b[2C\x1b[1FZ";
    output(&mut direct, 1, suffix).expect("direct suffix");
    output(&mut restored, 1, suffix).expect("restored suffix");
    direct
        .apply_event(Event::Resize {
            source_sequence: 2,
            columns: 6,
            rows: 3,
        })
        .expect("direct shrink");
    restored
        .apply_event(Event::Resize {
            source_sequence: 2,
            columns: 6,
            rows: 3,
        })
        .expect("restored shrink");
    output(&mut direct, 3, b"\x1b[?69lQ").expect("direct mode reset suffix");
    output(&mut restored, 3, b"\x1b[?69lQ").expect("restored mode reset suffix");
    assert_eq!(restored, direct);
    assert_eq!(
        restored
            .export_snapshot_v2()
            .expect("restored final snapshot"),
        direct.export_snapshot_v2().expect("direct final snapshot")
    );
    assert_eq!(
        restored
            .export_render_frame_v1()
            .expect("restored V1 frame"),
        direct.export_render_frame_v1().expect("direct V1 frame")
    );
    assert_eq!(
        restored
            .export_render_frame_v2()
            .expect("restored V2 frame"),
        direct.export_render_frame_v2().expect("direct V2 frame")
    );

    let direct_effects = direct
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 4,
            bytes: b"\x1b[6n\x1b[?6n",
        })
        .expect("direct CPR effects")
        .host_effect_frame_v1()
        .to_vec();
    let restored_effects = restored
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 4,
            bytes: b"\x1b[6n\x1b[?6n",
        })
        .expect("restored CPR effects")
        .host_effect_frame_v1()
        .to_vec();
    assert_eq!(restored_effects, direct_effects);
}

#[test]
fn declrmm_and_decslrm_are_deterministic_across_every_output_split() {
    let bytes = b"\x1b[?69h\x1b[2;7s\x1b[?6h\x1b[2;3H";
    let mut expected = Terminal::new(8, 4).expect("terminal");
    output(&mut expected, 0, bytes).expect("joined DECLRMM stream");
    output(&mut expected, 1, b"").expect("matching event count");

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(8, 4).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("DECLRMM prefix");
        output(&mut actual, 1, &bytes[split..]).expect("DECLRMM suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }

    let mut incomplete = Terminal::new(8, 4).expect("terminal");
    output(&mut incomplete, 0, b"\x1b[?69h\x1b[2;").expect("incomplete DECSLRM");
    assert_eq!(
        incomplete.export_snapshot_v2(),
        Err(TerminalError::SnapshotNotEligible)
    );
}

#[test]
fn resize_resets_custom_margins_that_collapse_below_two_cells() {
    let mut terminal = Terminal::new(6, 4).expect("terminal");
    output(&mut terminal, 0, b"\x1b[3;4r\x1b[?69h\x1b[5;6s").expect("two-cell margins");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 5,
            rows: 3,
        })
        .expect("collapse both custom axes");

    assert!(terminal.modes.left_right_margin);
    assert_eq!(terminal.margins, Margins::full(5, 3));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn declrmm_resize_preserves_custom_right_delayed_wrap_across_snapshot_restore() {
    let mut direct = Terminal::new(8, 4).expect("terminal");
    output(&mut direct, 0, b"\x1b[?69h\x1b[2;7s\x1b[2;2HABCDEF")
        .expect("custom-right delayed wrap");
    assert_eq!(direct.cursor(), (1, 6));
    assert!(direct.active_screen().wrap_pending);

    let snapshot = direct.export_snapshot_v2().expect("pending-wrap snapshot");
    let mut restored = Terminal::import_snapshot_v2(&snapshot).expect("restore pending wrap");
    for terminal in [&mut direct, &mut restored] {
        terminal
            .apply_event(Event::Resize {
                source_sequence: 1,
                columns: 8,
                rows: 5,
            })
            .expect("change only height without changing horizontal margins");
        assert!(terminal.active_screen().wrap_pending);
        output(terminal, 2, b"G").expect("trigger preserved delayed wrap");
        assert_eq!(terminal.cursor(), (2, 2));
        assert_eq!(terminal.cell(2, 1).expect("wrapped G").character, 'G');
    }
    assert_eq!(restored, direct);
    assert_snapshot_self_closure(&direct);
}

#[test]
fn declrmm_snapshot_import_skips_inapplicable_frozen_primary_reflow_limit() {
    let options = TerminalOptions {
        history_capacity_rows: 1,
    };
    let mut terminal = Terminal::new_with_options(1, 512, 0, options).expect("history terminal");
    output(&mut terminal, 0, b"\x1b[512;1HX\n").expect("capture one primary history row");
    assert_eq!(terminal.main.stored_rows(), 513);
    output(&mut terminal, 1, b"\x1b[?69h\x1b[?1049h")
        .expect("freeze primary while DECLRMM selects crop semantics");
    terminal
        .apply_event(Event::Resize {
            source_sequence: 2,
            columns: 512,
            rows: 512,
        })
        .expect("expand active alternate screen");

    let snapshot = terminal
        .export_snapshot_v2()
        .expect("legal frozen-primary snapshot");
    let restored =
        Terminal::import_snapshot_v2(&snapshot).expect("crop-restorable primary must import");
    assert_eq!(
        restored.export_snapshot_v2().expect("canonical re-export"),
        snapshot
    );
}

#[test]
fn snapshot_export_rejects_internally_inconsistent_declrmm_margins() {
    let mut disabled = Terminal::new(8, 4).expect("terminal");
    disabled.margins.horizontal_follows_screen = false;
    assert_eq!(
        disabled.export_snapshot_v2(),
        Err(TerminalError::MalformedSnapshot)
    );

    let mut invalid_bounds = Terminal::new(8, 4).expect("terminal");
    invalid_bounds.modes.left_right_margin = true;
    invalid_bounds.margins.left = 7;
    invalid_bounds.margins.right = 7;
    invalid_bounds.margins.horizontal_follows_screen = false;
    assert_eq!(
        invalid_bounds.export_snapshot_v2(),
        Err(TerminalError::MalformedSnapshot)
    );
}

#[test]
fn declrmm_print_wrap_and_checkpoint_use_the_right_margin_without_touching_outer_cells() {
    let mut terminal = Terminal::new(8, 4).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[1;1H11111111\x1b[2;1H22222222\x1b[?69h\x1b[2;7s\x1b[2;4r\x1b[2;2HABCDEF\x1b7",
    )
    .expect("write to the horizontal right margin");
    assert_eq!(terminal.cursor(), (1, 6));
    assert!(terminal.active_screen().wrap_pending);
    assert_eq!(row_text(&terminal, 1), "2ABCDEF2");
    assert_snapshot_self_closure(&terminal);
    let snapshot = terminal.export_snapshot_v2().expect("custom wrap snapshot");
    let main_meta = snapshot::section_payload_offset_for_test(&snapshot, 8);
    assert_snapshot_mutation_is_malformed(
        &snapshot,
        "active cursor wrap pending at the physical edge instead of the right margin",
        |bytes| {
            bytes[main_meta + 20..main_meta + 24].copy_from_slice(&7_u32.to_le_bytes());
        },
    );
    assert_snapshot_mutation_is_malformed(
        &snapshot,
        "saved cursor wrap pending at the physical edge instead of the right margin",
        |bytes| {
            bytes[main_meta + 36..main_meta + 40].copy_from_slice(&7_u32.to_le_bytes());
        },
    );

    output(&mut terminal, 1, b"G").expect("trigger delayed margin wrap");
    assert_eq!(terminal.cursor(), (2, 2));
    assert_eq!(row_text(&terminal, 1), "2ABCDEF2");
    assert_eq!(row_text(&terminal, 2), " G      ");
    assert!(!terminal.active_screen().soft_wrapped[1]);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn declrmm_lf_ri_scroll_and_line_edits_are_rectangular_and_history_neutral() {
    let options = TerminalOptions {
        history_capacity_rows: 4,
    };
    let mut lf = Terminal::new_with_options(8, 5, 0, options).expect("history terminal");
    output(
        &mut lf,
        0,
        b"\x1b[1;1H00000000\x1b[2;1H11111111\x1b[3;1H22222222\x1b[4;1H33333333\x1b[5;1H44444444\x1b[?69h\x1b[3;6s\x1b[2;4r\x1b[4;5H\n",
    )
    .expect("LF at the bottom of a rectangular region");
    assert_eq!(row_text(&lf, 0), "00000000");
    assert_eq!(row_text(&lf, 1), "11222211");
    assert_eq!(row_text(&lf, 2), "22333322");
    assert_eq!(row_text(&lf, 3), "33    33");
    assert_eq!(row_text(&lf, 4), "44444444");
    assert_eq!(lf.main.viewport_top(), 0);

    let mut ri = Terminal::new(8, 5).expect("terminal");
    output(
        &mut ri,
        0,
        b"\x1b[1;1H00000000\x1b[2;1H11111111\x1b[3;1H22222222\x1b[4;1H33333333\x1b[5;1H44444444\x1b[?69h\x1b[3;6s\x1b[2;4r\x1b[2;5H\x1bM",
    )
    .expect("RI at the top of a rectangular region");
    assert_eq!(row_text(&ri, 1), "11    11");
    assert_eq!(row_text(&ri, 2), "22111122");
    assert_eq!(row_text(&ri, 3), "33222233");

    let baseline = b"\x1b[1;1H00000000\x1b[2;1H11111111\x1b[3;1H22222222\x1b[4;1H33333333\x1b[5;1H44444444\x1b[?69h\x1b[3;6s\x1b[2;4r";
    for (command, expected) in [
        (b"\x1b[2;4H\x1b[L".as_slice(), "11    11"),
        (b"\x1b[2;4H\x1b[M".as_slice(), "11222211"),
        (b"\x1b[S".as_slice(), "11222211"),
        (b"\x1b[T".as_slice(), "11    11"),
    ] {
        let mut terminal = Terminal::new(8, 5).expect("terminal");
        output(&mut terminal, 0, baseline).expect("rectangular baseline");
        output(&mut terminal, 1, command).expect("rectangular line operation");
        assert_eq!(row_text(&terminal, 1), expected, "command {command:?}");
        assert_eq!(row_text(&terminal, 0), "00000000");
        assert_eq!(row_text(&terminal, 4), "44444444");
        assert_snapshot_self_closure(&terminal);
    }
}

#[test]
fn declrmm_character_edits_and_scroll_repair_wide_margin_boundaries() {
    let mut edited = Terminal::new(8, 3).expect("terminal");
    output(
        &mut edited,
        0,
        b"\x1b[1;1HABCDEFGH\x1b[?69h\x1b[3;7s\x1b[1;3H\x1b[2@\x1b[P\x1b[X",
    )
    .expect("margin-scoped character edits");
    assert_eq!(row_text(&edited, 0), "AB CDE H");

    let mut wide = Terminal::new(6, 3).expect("terminal");
    output(&mut wide, 0, "\x1b[1;1H你\x1b[2;1Habcdef".as_bytes()).expect("wide boundary baseline");
    output(&mut wide, 1, b"\x1b[?69h\x1b[2;5s\x1b[S")
        .expect("scroll through a wide continuation boundary");
    wide.active_screen()
        .validate()
        .expect("rectangle scroll keeps wide cells canonical");
    assert!(!wide.cell(0, 0).expect("left boundary cell").continuation);
    assert!(!wide.cell(0, 1).expect("inside boundary cell").continuation);
    assert_snapshot_self_closure(&wide);
}

#[test]
fn dec_column_edits_default_zero_and_clamped_counts_are_canonical() {
    let fixture = || {
        let mut terminal = Terminal::new(6, 3).expect("terminal");
        output(
            &mut terminal,
            0,
            b"\x1b[1;1HABCDEF\x1b[2;1Hghijkl\x1b[3;1HMNOPQR\x1b[2;3H",
        )
        .expect("column-edit fixture");
        terminal
    };

    for (name, default, zero, expected) in [
        (
            "DECIC",
            b"\x1b['}".as_slice(),
            b"\x1b[0'}".as_slice(),
            ["AB CDE", "gh ijk", "MN OPQ"],
        ),
        (
            "DECDC",
            b"\x1b['~".as_slice(),
            b"\x1b[0'~".as_slice(),
            ["ABDEF ", "ghjkl ", "MNPQR "],
        ),
    ] {
        let mut default_terminal = fixture();
        output(&mut default_terminal, 1, default).expect(name);
        let mut zero_terminal = fixture();
        output(&mut zero_terminal, 1, zero).expect(name);

        assert_eq!(default_terminal, zero_terminal, "{name} Ps=0");
        for (row, expected) in expected.into_iter().enumerate() {
            assert_eq!(
                row_text(&default_terminal, u32::try_from(row).expect("bounded row")),
                expected,
                "{name} row {row}"
            );
        }
        assert_eq!(default_terminal.cursor(), (1, 2), "{name}");
        assert_eq!(default_terminal.next_source_sequence(), 2, "{name}");
        assert_snapshot_self_closure(&default_terminal);
    }

    for (name, command) in [
        ("DECIC", b"\x1b[999'}".as_slice()),
        ("DECDC", b"\x1b[999'~".as_slice()),
    ] {
        let mut terminal = fixture();
        output(&mut terminal, 1, command).expect(name);
        assert_eq!(row_text(&terminal, 0), "AB    ", "{name}");
        assert_eq!(row_text(&terminal, 1), "gh    ", "{name}");
        assert_eq!(row_text(&terminal, 2), "MN    ", "{name}");
        terminal
            .active_screen()
            .validate()
            .unwrap_or_else(|error| panic!("{name} result is canonical: {error:?}"));
    }
}

#[test]
fn dec_column_edits_use_the_cursor_column_across_only_the_active_scroll_rectangle() {
    const BASELINE: &[u8] = b"\x1b[1;1Habcdefgh\x1b[2;1HABCDEFGH\x1b[3;1H12345678\x1b[4;1H!@#$%^&*\x1b[5;1Hqrstuvwx\x1b[?69h\x1b[2;7s\x1b[2;4r\x1b[3;4H";

    for (name, command, expected_rows) in [
        (
            "DECIC",
            b"\x1b[2'}".as_slice(),
            ["abcdefgh", "ABC  DEH", "123  458", "!@#  $%*", "qrstuvwx"],
        ),
        (
            "DECDC",
            b"\x1b[2'~".as_slice(),
            ["abcdefgh", "ABCFG  H", "12367  8", "!@#^&  *", "qrstuvwx"],
        ),
    ] {
        let mut terminal = Terminal::new(8, 5).expect("terminal");
        output(&mut terminal, 0, BASELINE).expect("scroll rectangle fixture");
        output(&mut terminal, 1, command).expect(name);

        for (row, expected) in expected_rows.into_iter().enumerate() {
            assert_eq!(
                row_text(&terminal, u32::try_from(row).expect("bounded row")),
                expected,
                "{name} row {row}"
            );
        }
        assert_eq!(terminal.cursor(), (2, 3), "{name} cursor");
        assert_snapshot_self_closure(&terminal);
    }

    for (name, cursor, command) in [
        (
            "DECIC above",
            b"\x1b[1;4H".as_slice(),
            b"\x1b['}".as_slice(),
        ),
        ("DECDC left", b"\x1b[3;1H".as_slice(), b"\x1b['~".as_slice()),
    ] {
        let mut terminal = Terminal::new(8, 5).expect("terminal");
        output(&mut terminal, 0, BASELINE).expect("scroll rectangle fixture");
        output(&mut terminal, 1, cursor).expect("outside cursor");
        let before_rows: Vec<_> = (0..5).map(|row| row_text(&terminal, row)).collect();
        output(&mut terminal, 2, command).expect(name);
        let after_rows: Vec<_> = (0..5).map(|row| row_text(&terminal, row)).collect();
        assert_eq!(after_rows, before_rows, "{name}");
        assert_snapshot_self_closure(&terminal);
    }
}

#[test]
fn dec_column_edits_use_bce_and_repair_wide_boundaries_on_every_affected_row() {
    const BASELINE: &str = "\u{1b}[1;1HA界BCDE\u{1b}[2;1HABCDE界\u{1b}[3;1H1234567\u{1b}[?69h\u{1b}[2;6s\u{1b}[1;3r\u{1b}[1;2;3;4:4;31;44m\u{1b}[2;3H";
    let erase_attributes = Attributes {
        foreground: Color::Indexed(1),
        background: Color::Indexed(4),
        ..Attributes::default()
    };

    for (name, command, expected_rows, bce_column) in [
        (
            "DECIC",
            b"\x1b['}".as_slice(),
            ["A   BCE", "AB CDE ", "12 3457"],
            2,
        ),
        (
            "DECDC",
            b"\x1b['~".as_slice(),
            ["A BCD E", "ABDE   ", "12456 7"],
            5,
        ),
    ] {
        let mut terminal = Terminal::new(7, 3).expect("terminal");
        output(&mut terminal, 0, BASELINE.as_bytes()).expect("wide BCE fixture");
        output(&mut terminal, 1, command).expect(name);

        for (row, expected) in expected_rows.into_iter().enumerate() {
            assert_eq!(
                row_text(&terminal, u32::try_from(row).expect("bounded row")),
                expected,
                "{name} row {row}"
            );
        }
        let blank = terminal.cell(2, bce_column).expect("BCE blank");
        assert_eq!(blank.character, ' ', "{name}");
        assert_eq!(blank.attributes, erase_attributes, "{name}");
        assert!(!blank.protected, "{name}");
        assert_eq!(blank.hyperlink, None, "{name}");
        terminal
            .active_screen()
            .validate()
            .unwrap_or_else(|error| panic!("{name} wide repair is canonical: {error:?}"));
        assert_snapshot_self_closure(&terminal);
    }
}

#[test]
fn dec_column_edits_preserve_terminal_state_and_history_but_cancel_delayed_wrap() {
    let options = TerminalOptions {
        history_capacity_rows: 2,
    };
    let mut terminal = Terminal::new_with_options(7, 3, 0, options).expect("history terminal");
    output(&mut terminal, 0, b"A\r\nB\r\nC\r\nD").expect("create history");
    output(
        &mut terminal,
        1,
        b"\x1b]2;DEC columns\x07\x1b[?1h\x1b[1;2;3;4:4;31;44m\x1b[1;7H\x1b7\x1b[?69h\x1b[2;6s\x1b[1;3r\x1b[2;3H",
    )
    .expect("state fixture");

    let viewport_top = terminal.main.viewport_top();
    assert!(viewport_top > 0);
    let history_cell_count =
        usize::try_from(viewport_top * terminal.columns).expect("bounded history cell count");
    let history_cells = terminal.main.cells[..history_cell_count].to_vec();
    let history_rows = usize::try_from(viewport_top).expect("bounded history rows");
    let history_metadata = terminal.main.row_metadata[..history_rows].to_vec();
    let history_meaningful = terminal.main.meaningful_end[..history_rows].to_vec();
    let history_wrapping = terminal.main.soft_wrapped[..history_rows].to_vec();
    let cursor = terminal.cursor();
    let attributes = terminal.active_screen().attributes;
    let saved = terminal.active_screen().saved;
    let modes = terminal.modes;
    let osc = terminal.osc.clone();
    let charset = terminal.charset;

    for (name, command) in [
        ("DECIC", b"\x1b[2'}".as_slice()),
        ("DECDC", b"\x1b[2'~".as_slice()),
    ] {
        let mut edited = terminal.clone();
        output(&mut edited, 2, command).expect(name);

        assert_eq!(edited.main.viewport_top(), viewport_top, "{name}");
        assert_eq!(
            &edited.main.cells[..history_cell_count],
            history_cells,
            "{name}"
        );
        assert_eq!(
            &edited.main.row_metadata[..history_rows],
            history_metadata,
            "{name}"
        );
        assert_eq!(
            &edited.main.meaningful_end[..history_rows],
            history_meaningful,
            "{name}"
        );
        assert_eq!(
            &edited.main.soft_wrapped[..history_rows],
            history_wrapping,
            "{name}"
        );
        assert_eq!(edited.cursor(), cursor, "{name}");
        assert_eq!(edited.active_screen().attributes, attributes, "{name}");
        assert_eq!(edited.active_screen().saved, saved, "{name}");
        assert_eq!(edited.modes, modes, "{name}");
        assert_eq!(edited.osc, osc, "{name}");
        assert_eq!(edited.charset, charset, "{name}");
        assert_eq!(edited.next_source_sequence(), 3, "{name}");
        assert_eq!(edited.osc_state().window_title(), "DEC columns", "{name}");
    }

    for (name, command) in [
        ("DECIC", b"\x1b['}".as_slice()),
        ("DECDC", b"\x1b['~".as_slice()),
    ] {
        let mut wrapped = Terminal::new(3, 2).expect("terminal");
        output(&mut wrapped, 0, b"ABC").expect("delayed wrap");
        assert!(wrapped.active_screen().wrap_pending, "{name}");
        output(&mut wrapped, 1, command).expect(name);
        assert_eq!(wrapped.cursor(), (0, 2), "{name}");
        assert!(!wrapped.active_screen().wrap_pending, "{name}");
        output(&mut wrapped, 2, b"X").expect("same-line suffix");
        assert_eq!(row_text(&wrapped, 0), "ABX", "{name}");
    }
}

#[test]
fn dec_column_edits_reject_noncanonical_shapes_with_event_and_batch_rollback() {
    for (name, bytes) in [
        ("DECIC private", b"\x1b[?1'}".as_slice()),
        ("DECIC subparameter", b"\x1b[1:2'}".as_slice()),
        ("DECIC multiple", b"\x1b[1;2'}".as_slice()),
        ("DECIC intermediates", b"\x1b[1$'}".as_slice()),
        ("DECDC private", b"\x1b[?1'~".as_slice()),
        ("DECDC subparameter", b"\x1b[1:2'~".as_slice()),
        ("DECDC multiple", b"\x1b[1;2'~".as_slice()),
        ("DECDC intermediates", b"\x1b[1$'~".as_slice()),
    ] {
        let mut terminal = Terminal::new(6, 3).expect("terminal");
        output(&mut terminal, 0, b"ABCDEF\x1b[2;3H").expect("baseline");
        let before = terminal.clone();
        assert!(output(&mut terminal, 1, bytes).is_err(), "{name}");
        let mut expected = before;
        expected.failed_closed = true;
        assert_eq!(terminal, expected, "{name}");
        assert_eq!(terminal.next_source_sequence(), 1, "{name}");
    }

    let mut event = Terminal::new(6, 3).expect("terminal");
    output(&mut event, 0, b"ABCDEF\x1b[2;3H").expect("baseline");
    let mut expected_event = event.clone();
    expected_event.failed_closed = true;
    assert_eq!(
        output(&mut event, 1, b"\x1b['}X\x1b[1;2'~"),
        Err(TerminalError::Unsupported("DECDC parameters"))
    );
    assert_eq!(event, expected_event);

    let mut batch = Terminal::new(6, 3).expect("terminal");
    output(&mut batch, 0, b"ABCDEF\x1b[2;3H").expect("baseline");
    let mut expected_batch = batch.clone();
    expected_batch.failed_closed = true;
    assert_eq!(
        batch.apply_events_atomic(&[
            Event::Output {
                source_sequence: 1,
                bytes: b"\x1b[2'}",
            },
            Event::Output {
                source_sequence: 2,
                bytes: b"\x1b[1;2'~",
            },
        ]),
        Err(TerminalError::Unsupported("DECDC parameters"))
    );
    assert_eq!(batch, expected_batch);
}

#[test]
fn dec_column_edits_are_deterministic_at_every_output_split() {
    let bytes = b"\x1b[1;1HABCDEF\x1b[2;1Hghijkl\x1b[3;1HMNOPQR\x1b[2;3H\x1b[2'}\x1b[1'~";
    let mut expected = Terminal::new(6, 3).expect("terminal");
    output(&mut expected, 0, bytes).expect("unsplit column edits");
    output(&mut expected, 1, b"").expect("matching event count");

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(6, 3).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("column-edit prefix");
        output(&mut actual, 1, &bytes[split..]).expect("column-edit suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }
}

#[test]
fn declrmm_applies_on_alternate_screen_and_ris_restores_full_screen_defaults() {
    let mut terminal = Terminal::new(8, 4).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[?69h\x1b[2;7s\x1b[2;4r\x1b[?1049h\x1b[2;2HABCDEF",
    )
    .expect("alternate DECLRMM output");
    assert!(terminal.is_alternate_screen_active());
    assert_eq!(terminal.cursor(), (1, 6));
    assert!(terminal.active_screen().wrap_pending);
    output(&mut terminal, 1, b"G").expect("alternate margin wrap");
    assert_eq!(terminal.cursor(), (2, 2));

    output(&mut terminal, 2, b"\x1bc").expect("RIS");
    assert!(!terminal.is_alternate_screen_active());
    assert!(!terminal.modes.left_right_margin);
    assert!(!terminal.modes.origin_mode);
    assert_eq!(terminal.margins, Margins::full(8, 4));
    assert_eq!(terminal.cursor(), (0, 0));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn declrmm_cursor_moves_clamp_only_when_the_cursor_intersects_the_margin_axes() {
    let mut terminal = Terminal::new(8, 5).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[?69h\x1b[3;6s\x1b[2;4r\x1b[3;5H\x1b[99E",
    )
    .expect("CNL inside margins");
    assert_eq!(terminal.cursor(), (3, 2));
    output(&mut terminal, 1, b"\x1b[3;5H\x1b[99F").expect("CPL inside margins");
    assert_eq!(terminal.cursor(), (1, 2));
    output(&mut terminal, 2, b"\x1b[1;5H\x1b[99F")
        .expect("CPL above margins remains screen-relative");
    assert_eq!(terminal.cursor(), (0, 0));

    output(&mut terminal, 3, b"\x1b[3;5H\x1b[99C")
        .expect("CUF inside the rectangle clamps at the right margin");
    assert_eq!(terminal.cursor(), (2, 5));
    output(&mut terminal, 4, b"\x1b[1;5H\x1b[99C")
        .expect("CUF above the rectangle reaches the screen edge");
    assert_eq!(terminal.cursor(), (0, 7));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn hpa_hpr_and_vpr_match_cha_cuf_and_cud_parameters_and_boundaries() {
    let cases: &[CursorAliasCase<'_>] = &[
        (b"\x1b[3;5H\x1b[`", b"\x1b[3;5H\x1b[G", (2, 0)),
        (b"\x1b[3;5H\x1b[0`", b"\x1b[3;5H\x1b[0G", (2, 0)),
        (b"\x1b[3;5H\x1b[4`", b"\x1b[3;5H\x1b[4G", (2, 3)),
        (b"\x1b[3;5H\x1b[999`", b"\x1b[3;5H\x1b[999G", (2, 7)),
        (b"\x1b[2;4H\x1b[a", b"\x1b[2;4H\x1b[C", (1, 4)),
        (b"\x1b[2;4H\x1b[0a", b"\x1b[2;4H\x1b[0C", (1, 4)),
        (b"\x1b[2;4H\x1b[3a", b"\x1b[2;4H\x1b[3C", (1, 6)),
        (b"\x1b[2;4H\x1b[999a", b"\x1b[2;4H\x1b[999C", (1, 7)),
        (b"\x1b[2;4H\x1b[e", b"\x1b[2;4H\x1b[B", (2, 3)),
        (b"\x1b[2;4H\x1b[0e", b"\x1b[2;4H\x1b[0B", (2, 3)),
        (b"\x1b[2;4H\x1b[2e", b"\x1b[2;4H\x1b[2B", (3, 3)),
        (b"\x1b[2;4H\x1b[999e", b"\x1b[2;4H\x1b[999B", (4, 3)),
    ];

    for &(alias_bytes, canonical_bytes, expected_cursor) in cases {
        let mut alias = Terminal::new(8, 5).expect("alias terminal");
        output(&mut alias, 0, alias_bytes).expect("cursor alias");
        let mut canonical = Terminal::new(8, 5).expect("canonical terminal");
        output(&mut canonical, 0, canonical_bytes).expect("canonical cursor command");

        assert_eq!(alias.cursor(), expected_cursor, "alias {alias_bytes:?}");
        assert_eq!(alias, canonical, "alias {alias_bytes:?}");
        assert_snapshot_self_closure(&alias);
    }
}

#[test]
fn hpa_hpr_and_vpr_inherit_cha_cuf_and_cud_margin_semantics() {
    let cases: &[CursorAliasCase<'_>] = &[
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[?6h\x1b[1;1H\x1b[99`",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[?6h\x1b[1;1H\x1b[99G",
            (1, 5),
        ),
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[?6h\x1b[1;1H\x1b[99a",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[?6h\x1b[1;1H\x1b[99C",
            (1, 5),
        ),
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[?6h\x1b[1;1H\x1b[99e",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[?6h\x1b[1;1H\x1b[99B",
            (3, 2),
        ),
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;5H\x1b[1`",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;5H\x1b[1G",
            (2, 0),
        ),
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[1;5H\x1b[99a",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[1;5H\x1b[99C",
            (0, 7),
        ),
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;1H\x1b[99e",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;1H\x1b[99B",
            (4, 0),
        ),
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;5H\x1b[99a",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;5H\x1b[99C",
            (2, 5),
        ),
        (
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;5H\x1b[99e",
            b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[3;5H\x1b[99B",
            (3, 4),
        ),
    ];

    for &(alias_bytes, canonical_bytes, expected_cursor) in cases {
        let mut alias = Terminal::new(8, 5).expect("alias terminal");
        output(&mut alias, 0, alias_bytes).expect("margin-aware cursor alias");
        let mut canonical = Terminal::new(8, 5).expect("canonical terminal");
        output(&mut canonical, 0, canonical_bytes).expect("canonical margin-aware command");

        assert_eq!(alias.cursor(), expected_cursor, "alias {alias_bytes:?}");
        assert_eq!(alias, canonical, "alias {alias_bytes:?}");
        assert_snapshot_self_closure(&alias);
    }
}

#[test]
fn hpa_hpr_and_vpr_are_deterministic_at_every_split_and_bytewise_event() {
    let bytes = b"\x1b[3;5H\x1b[0`\x1b[a\x1b[2e";
    let mut expected_two_events = Terminal::new(8, 5).expect("terminal");
    output(&mut expected_two_events, 0, bytes).expect("joined cursor aliases");
    output(&mut expected_two_events, 1, b"").expect("matching event count");
    assert_eq!(expected_two_events.cursor(), (4, 1));

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(8, 5).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("cursor alias prefix");
        output(&mut actual, 1, &bytes[split..]).expect("cursor alias suffix");
        assert_eq!(actual, expected_two_events, "split at byte {split}");
    }

    let mut expected_byte_events = Terminal::new(8, 5).expect("terminal");
    output(&mut expected_byte_events, 0, bytes).expect("joined cursor aliases");
    for sequence in 1..bytes.len() {
        output(
            &mut expected_byte_events,
            u64::try_from(sequence).expect("test sequence"),
            b"",
        )
        .expect("matching empty event");
    }
    let mut bytewise = Terminal::new(8, 5).expect("terminal");
    for (sequence, byte) in bytes.iter().enumerate() {
        output(
            &mut bytewise,
            u64::try_from(sequence).expect("test sequence"),
            std::slice::from_ref(byte),
        )
        .expect("one-byte cursor alias event");
    }
    assert_eq!(bytewise, expected_byte_events);
    assert_snapshot_self_closure(&bytewise);
}

#[test]
fn hpa_hpr_and_vpr_snapshot_suffix_matches_canonical_commands_and_frozen_frames() {
    let mut direct = Terminal::new(8, 5).expect("terminal");
    output(
        &mut direct,
        0,
        b"\x1b[2;4r\x1b[?69h\x1b[3;6s\x1b[?6h\x1b[1;1H",
    )
    .expect("cursor margin checkpoint");
    let checkpoint = direct
        .export_snapshot_v2()
        .expect("cursor alias checkpoint");
    let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("restore checkpoint");
    let mut canonical = Terminal::import_snapshot_v2(&checkpoint).expect("canonical checkpoint");

    output(&mut direct, 1, b"\x1b[2`\x1b[1a\x1b[1e").expect("direct alias suffix");
    output(&mut restored, 1, b"\x1b[2`\x1b[1a\x1b[1e").expect("restored alias suffix");
    output(&mut canonical, 1, b"\x1b[2G\x1b[1C\x1b[1B").expect("canonical cursor suffix");

    assert_eq!(restored, direct);
    assert_eq!(canonical, direct);
    assert_eq!(
        restored.export_snapshot_v2().expect("restored snapshot"),
        direct.export_snapshot_v2().expect("direct snapshot")
    );
    assert_eq!(
        canonical.export_snapshot_v2().expect("canonical snapshot"),
        direct.export_snapshot_v2().expect("direct snapshot")
    );
    assert_eq!(
        canonical.export_render_frame_v1().expect("canonical V1"),
        direct.export_render_frame_v1().expect("alias V1")
    );
    assert_eq!(
        canonical.export_render_frame_v2().expect("canonical V2"),
        direct.export_render_frame_v2().expect("alias V2")
    );
    assert_eq!(
        canonical.export_render_frame_v3().expect("canonical V3"),
        direct.export_render_frame_v3().expect("alias V3")
    );
}

#[test]
fn cpr_column_is_relative_only_when_decom_and_declrmm_are_both_active() {
    let mut terminal = Terminal::new(8, 5).expect("terminal");
    output(&mut terminal, 0, b"\x1b[2;4r\x1b[?69h\x1b[3;7s\x1b[3;6H")
        .expect("DECLRMM without DECOM uses absolute addressing");
    let effects = terminal
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 1,
            bytes: b"\x1b[6n\x1b[?6n",
        })
        .expect("absolute CPR pair");
    assert_eq!(
        host_effect_payloads(effects.host_effect_frame_v1()),
        [b"\x1b[3;6R".as_slice(), b"\x1b[?3;6R".as_slice()]
    );
    effects
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("commit absolute CPR pair");

    output(&mut terminal, 2, b"\x1b[?6h\x1b[2;3H").expect("DECOM and DECLRMM relative addressing");
    let effects = terminal
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 3,
            bytes: b"\x1b[6n\x1b[?6n",
        })
        .expect("relative CPR pair");
    assert_eq!(
        host_effect_payloads(effects.host_effect_frame_v1()),
        [b"\x1b[2;3R".as_slice(), b"\x1b[?2;3R".as_slice()]
    );
}

#[test]
fn conpty_non_private_cursor_visibility_alias_matches_dectcem() {
    let mut terminal = Terminal::new(6, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b[25l").expect("ConPTY cursor hide alias");
    assert!(!terminal.cursor_visible());
    output(&mut terminal, 1, b"\x1b[25h").expect("ConPTY cursor show alias");
    assert!(terminal.cursor_visible());
    output(&mut terminal, 2, b"\x1b[?25l").expect("DEC cursor hide");
    assert!(!terminal.cursor_visible());
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn xtmodkeys_is_strict_atomic_split_safe_and_xtqmodkeys_is_reply_only() {
    let bytes = b"\x1b[>4;1m\x1b[>4;2m\x1b[>4;3m\x1b[>4m";
    let mut expected = Terminal::new(4, 1).expect("terminal");
    output(&mut expected, 0, bytes).expect("joined XTMODKEYS");
    output(&mut expected, 1, b"").expect("matching event count");
    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(4, 1).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("XTMODKEYS prefix");
        output(&mut actual, 1, &bytes[split..]).expect("XTMODKEYS suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }
    assert_eq!(
        expected.modes.modify_other_keys,
        modes::ModifyOtherKeys::Disabled
    );

    for invalid in [
        &b"\x1b[>4;4m"[..],
        &b"\x1b[>4;1;2m"[..],
        &b"\x1b[>5;1m"[..],
        &b"\x1b[>4h"[..],
    ] {
        let mut terminal = Terminal::new(4, 1).expect("terminal");
        output(&mut terminal, 0, b"\x1b[>4;2m").expect("baseline XTMODKEYS");
        let mut rolled_back = terminal.clone();
        rolled_back.failed_closed = true;
        assert!(matches!(
            output(&mut terminal, 1, invalid),
            Err(TerminalError::Unsupported(_))
        ));
        assert_eq!(terminal, rolled_back, "invalid sequence {invalid:?}");
    }

    let mut query = Terminal::new(4, 1).expect("terminal");
    output(&mut query, 0, b"\x1b[?4m").expect("implemented XTQMODKEYS query");
    assert_eq!(
        output(&mut query, 1, b"\x1b[?5m"),
        Err(TerminalError::Unsupported("XTQMODKEYS parameters"))
    );
    assert!(query.is_failed_closed());
}

#[test]
fn primary_and_secondary_device_attributes_are_exact_reply_only_queries() {
    let mut terminal = Terminal::new(4, 1).expect("terminal");
    output(&mut terminal, 0, b"\x1b[c\x1b[0c\x1b[>c\x1b[>0c").expect("default DA queries");
    assert_snapshot_self_closure(&terminal);

    for bytes in [b"\x1b[1c".as_slice(), b"\x1b[>1c".as_slice()] {
        let mut invalid = Terminal::new(4, 1).expect("terminal");
        assert!(matches!(
            output(&mut invalid, 0, bytes),
            Err(TerminalError::Unsupported(_))
        ));
        assert!(invalid.is_failed_closed());
    }
}

#[test]
fn captured_host_replies_preserve_wire_order_offsets_and_canonical_payloads() {
    let bytes = b"\x1b[c\x1b[>0c\x1b[5n\x1b[6n\x1b[?5n\x1b[?6n";
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    let prepared = terminal
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 0,
            bytes,
        })
        .expect("prepare all reply families");
    let frame = prepared.host_effect_frame_v1().to_vec();
    assert_eq!(&frame[..8], b"TLHEF1\0\0");
    assert_eq!(&frame[8..16], &[1, 0, 0, 0, 64, 0, 40, 0]);
    assert_eq!(u32_at(&frame, 16), 6);
    assert_eq!(
        usize::try_from(u32_at(&frame, 20)).expect("total length"),
        frame.len()
    );
    assert_eq!(u64_at(&frame, 24), 64);
    assert_eq!(u64_at(&frame, 32), 64 + 6 * 40);
    assert_eq!(u64_at(&frame, 40), 1);
    assert_eq!(u64_at(&frame, 48), 0);
    assert_eq!(u32_at(&frame, 56), crc32c::checksum(&frame[64..]));
    assert_eq!(u32_at(&frame, 60), crc32c::checksum(&frame[..60]));
    assert_eq!(
        host_effect_payloads(&frame),
        [
            b"\x1b[?1;2c".as_slice(),
            b"\x1b[>0;0;0c".as_slice(),
            b"\x1b[0n".as_slice(),
            b"\x1b[1;1R".as_slice(),
            b"\x1b[?0n".as_slice(),
            b"\x1b[?1;1R".as_slice(),
        ]
    );
    for (index, expected_offset) in [3, 8, 12, 16, 21, 26].into_iter().enumerate() {
        let record = 64 + index * 40;
        assert_eq!(u32_at(&frame, record), 1);
        assert_eq!(u32_at(&frame, record + 4), 0);
        assert_eq!(u64_at(&frame, record + 8), 0);
        assert_eq!(u32_at(&frame, record + 16), expected_offset);
        assert_eq!(u32_at(&frame, record + 20), 0);
        assert_eq!(u32_at(&frame, record + 36), 0);
    }

    let mut published = None;
    prepared
        .commit_with(|owned| {
            published = Some(owned);
            Ok::<(), ()>(())
        })
        .expect("publisher succeeds");
    assert_eq!(terminal.next_source_sequence(), 1);
    assert_eq!(published.expect("owned published frame"), frame);
}

#[test]
fn host_effect_v2_captures_mode_and_dcs_queries_without_weakening_v1() {
    let bytes = concat!(
        "\x1b[4$p",
        "\x1b[?25$p",
        "\x1b[?9999$p",
        "\x1bP$qm\x1b\\",
        "\x1bP$qr\x1b\\",
        "\x1bP+q436f;524742\x1b\\"
    )
    .as_bytes();

    let mut v1 = Terminal::new(8, 2).expect("terminal");
    let before = v1.clone();
    assert!(matches!(
        v1.prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 0,
            bytes,
        }),
        Err(TerminalError::HostEffectVersionRequired)
    ));
    assert_eq!(v1, before);
    assert!(!v1.is_failed_closed());

    let mut terminal = Terminal::new(8, 2).expect("terminal");
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes,
        })
        .expect("prepare V2 queries");
    let frame = prepared.host_effect_frame_v2();
    assert_eq!(&frame[..8], b"TLHEF2\0\0");
    assert_eq!(u32_at(frame, 16), 6);
    assert_eq!(
        host_effect_payloads(frame),
        [
            b"\x1b[4;2$y".as_slice(),
            b"\x1b[?25;1$y".as_slice(),
            b"\x1b[?9999;0$y".as_slice(),
            b"\x1bP1$r0m\x1b\\".as_slice(),
            b"\x1bP1$r1;2r\x1b\\".as_slice(),
            b"\x1bP1+r436f=323536;524742=382f382f38\x1b\\".as_slice(),
        ]
    );
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("publish and commit V2");
    assert_eq!(terminal.next_source_sequence(), 1);

    let mut discard = Terminal::new(8, 2).expect("terminal");
    output(&mut discard, 0, bytes).expect("legacy discard apply accepts queries");
    assert_eq!(discard.next_source_sequence(), 1);
}

#[test]
fn color_scheme_reporting_mode_sets_queries_resets_and_is_split_safe() {
    let bytes = b"\x1b[?2031h\x1b[?2031$p\x1b[?2031l\x1b[?2031$p";
    let mut expected = Terminal::new(8, 2).expect("terminal");
    let prepared = expected
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes,
        })
        .expect("mode 2031 set, query, reset, and query");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v2()),
        [b"\x1b[?2031;1$y".as_slice(), b"\x1b[?2031;2$y".as_slice(),]
    );
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("publish and commit mode 2031 replies");
    assert!(!expected.modes.color_scheme_reporting);

    for split in 0..=bytes.len() {
        let mut actual = Terminal::new(8, 2).expect("terminal");
        output(&mut actual, 0, &bytes[..split]).expect("mode 2031 prefix");
        output(&mut actual, 1, &bytes[split..]).expect("mode 2031 suffix");
        assert_eq!(actual.modes, expected.modes, "split {split}");
        assert_eq!(actual.next_source_sequence(), 2, "split {split}");
    }
}

#[test]
fn xtversion_query_has_one_canonical_v2_reply_and_strict_parameters() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes: b"\x1b[>0q",
        })
        .expect("XTVERSION query");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v2()),
        [b"\x1bP>|TermLink(1)\x1b\\".as_slice()]
    );
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("publish and commit XTVERSION reply");

    for malformed in [&b"\x1b[>1q"[..], b"\x1b[>0;0q", b"\x1b[?0q", b"\x1b[0q"] {
        let mut malformed_terminal = Terminal::new(8, 2).expect("terminal");
        assert!(matches!(
            malformed_terminal.prepare_event_with_host_effects_v2(Event::Output {
                source_sequence: 0,
                bytes: malformed,
            }),
            Err(TerminalError::Unsupported(_))
        ));
        assert_eq!(
            malformed_terminal.next_source_sequence(),
            0,
            "malformed XTVERSION {malformed:?}"
        );
    }
}

#[test]
fn alternate_scroll_hint_preserves_replay_snapshot_and_input_modes() {
    // HOME-PC H122: entering the alternate screen emitted this exact prefix.
    let bytes = b"\x1b[?1049h\x1b[?1007h\x1b[1;1H\x1b[JALT\x1b[?1007l";
    let mut baseline = Terminal::new(8, 2).expect("terminal");
    output(&mut baseline, 0, b"MAIN").expect("primary content");
    let checkpoint = baseline.export_snapshot_v2().expect("pre-error checkpoint");
    output(&mut baseline, 1, b"\x1b[?1049h\x1b[1;1H\x1b[JALT").expect("baseline alternate screen");

    let mut terminal = Terminal::import_snapshot_v2(&checkpoint).expect("old checkpoint");
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 1,
            bytes,
        })
        .expect("alternate scroll hint must not retire the renderer");
    assert!(host_effect_payloads(prepared.host_effect_frame_v2()).is_empty());
    prepared.commit_with(|_| Ok::<(), ()>(())).expect("commit");
    assert_eq!(row_text(&terminal, 0), row_text(&baseline, 0));
    assert_eq!(terminal.cursor(), baseline.cursor());
    assert_eq!(terminal.modes, baseline.modes);
    assert_snapshot_self_closure(&terminal);

    for split in 0..=bytes.len() {
        let mut restored = Terminal::import_snapshot_v2(&checkpoint).expect("checkpoint");
        output(&mut restored, 1, &bytes[..split]).expect("prefix");
        output(&mut restored, 2, &bytes[split..]).expect("suffix");
        assert_eq!(row_text(&restored, 0), "ALT     ");
        assert_eq!(restored.modes, baseline.modes);
        output(&mut restored, 3, b"\x1b[?1049l").expect("leave alternate screen");
        assert_eq!(row_text(&restored, 0), "MAIN    ");
        assert_snapshot_self_closure(&restored);
    }

    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 2,
            bytes: b"\x1b[?1007;1004h\x1b[?1007$p\x1b[?1004$p",
        })
        .expect("hint in a supported mode list");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v2()),
        [b"\x1b[?1007;0$y".as_slice(), b"\x1b[?1004;1$y".as_slice()]
    );
    prepared.commit_with(|_| Ok::<(), ()>(())).expect("commit");

    for final_byte in *b"hl" {
        let mut unknown = Terminal::new(8, 2).expect("terminal");
        let mut mixed = b"\x1b[?1007;9999".to_vec();
        mixed.push(final_byte);
        assert_eq!(
            output(&mut unknown, 0, &mixed),
            Err(TerminalError::Unsupported("private mode"))
        );
        assert!(unknown.is_failed_closed());
        assert_eq!(unknown.next_source_sequence(), 0);
    }
}

#[test]
fn openconsole_startup_preserves_faint_and_color_queries() {
    let bytes =
        b"\x1b[1t\x1b[c\x1b[?1004h\x1b[?9001h\x1b[2mF\x1b[22mN\x1b]10;?\x1b\\\x1b]11;?\x1b\\";
    let mut expected = Terminal::new(8, 2).expect("terminal");
    output(&mut expected, 0, bytes).expect("OpenConsole startup");
    assert!(expected.cell(0, 0).expect("F").attributes.faint);
    assert!(!expected.cell(0, 1).expect("N").attributes.faint);
    assert_snapshot_self_closure(&expected);
    for split in 0..=bytes.len() {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        output(&mut terminal, 0, &bytes[..split]).expect("split prefix");
        output(&mut terminal, 1, &bytes[split..]).expect("split suffix");
        assert_eq!(terminal.cell(0, 0), expected.cell(0, 0));
        assert_eq!(terminal.cell(0, 1), expected.cell(0, 1));
    }
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes: b"\x1b[1t",
        })
        .expect("window restore hint");
    assert!(host_effect_payloads(prepared.host_effect_frame_v2()).is_empty());
    for malformed in [&b"\x1b[1;0t"[..], b"\x1b[2t"] {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        assert!(matches!(
            output(&mut terminal, 0, malformed),
            Err(TerminalError::Unsupported(_))
        ));
    }
}

#[test]
fn text_area_pixel_query_is_an_exact_no_reply_probe() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes: b"\x1b[14t",
        })
        .expect("text-area pixel query");
    assert!(host_effect_payloads(prepared.host_effect_frame_v2()).is_empty());
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("commit no-reply pixel query");
    assert_eq!(terminal.next_source_sequence(), 1);

    for malformed in [&b"\x1b[14;0t"[..], b"\x1b[13t"] {
        let mut malformed_terminal = Terminal::new(8, 2).expect("terminal");
        assert!(matches!(
            malformed_terminal.prepare_event_with_host_effects_v2(Event::Output {
                source_sequence: 0,
                bytes: malformed,
            }),
            Err(TerminalError::Unsupported(_))
        ));
        assert_eq!(malformed_terminal.next_source_sequence(), 0);
    }
}

#[test]
fn osc_color_queries_preserve_text_order_duplicates_and_v2_ordinals() {
    let baseline = OscBaseline {
        palette: [None; 256],
        foreground: Some(OscColor::new(0x12, 0x34, 0x56)),
        background: None,
        cursor: None,
    };
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    terminal.osc = OscState::new(baseline);
    let bytes = b"\x1b]4;1;#f00;1;?;1;#0f0;1;?\x1b\\\x1b]10;?\x1b\\";
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes,
        })
        .expect("ordered OSC queries");
    let frame = prepared.host_effect_frame_v2();
    assert_eq!(
        host_effect_payloads(frame),
        [
            b"\x1b]4;1;rgb:ffff/0000/0000\x1b\\".as_slice(),
            b"\x1b]4;1;rgb:0000/ffff/0000\x1b\\".as_slice(),
            b"\x1b]10;rgb:1212/3434/5656\x1b\\".as_slice(),
        ]
    );
    assert_eq!(u32_at(frame, 64 + 20), 0);
    assert_eq!(u32_at(frame, 104 + 20), 1);
    assert_eq!(u32_at(frame, 144 + 20), 0);
    assert_eq!(u64_at(frame, 64 + 8), 0);
    assert_eq!(u32_at(frame, 64 + 16), 27);
    assert_eq!(u32_at(frame, 104 + 16), 27);
    assert_eq!(u32_at(frame, 144 + 16), u32::try_from(bytes.len()).unwrap());
    prepared
        .publish(|_| Ok::<(), ()>(()))
        .expect("publish ordered OSC replies");
    assert_eq!(
        terminal.osc_state().palette(1),
        Some(OscColor::new(0, 255, 0))
    );
}

#[test]
fn unknown_xtgettcap_is_one_transactional_failure_reply() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes: b"\x1bP+q436f;4d73\x1b\\",
        })
        .expect("well-formed request with unknown capability");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v2()),
        [b"\x1bP0+r\x1b\\".as_slice()]
    );
}

#[test]
fn host_reply_capture_tracks_cross_event_final_bytes_and_batch_order() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    let events = [
        Event::Output {
            source_sequence: 0,
            bytes: b"\x1b[",
        },
        Event::Output {
            source_sequence: 1,
            bytes: b"c\x1b[5",
        },
        Event::Output {
            source_sequence: 2,
            bytes: b"n\x1b[>c",
        },
    ];
    let prepared = terminal
        .prepare_events_atomic_with_host_effects_v1(&events)
        .expect("cross-event queries");
    let frame = prepared.host_effect_frame_v1().to_vec();
    assert_eq!(
        host_effect_payloads(&frame),
        [
            b"\x1b[?1;2c".as_slice(),
            b"\x1b[0n".as_slice(),
            b"\x1b[>0;0;0c".as_slice(),
        ]
    );
    for (index, (sequence, offset)) in [(1, 1), (2, 1), (2, 5)].into_iter().enumerate() {
        let record = 64 + index * 40;
        assert_eq!(u64_at(&frame, record + 8), sequence);
        assert_eq!(u32_at(&frame, record + 16), offset);
        assert_eq!(u32_at(&frame, record + 20), 0);
    }
    prepared
        .publish(|owned| {
            assert_eq!(owned, frame);
            Ok::<(), ()>(())
        })
        .expect("publish then commit");
    assert_eq!(terminal.next_source_sequence(), 3);
}

#[test]
fn kitty_keyboard_protocol_operations_mask_default_and_bound_the_stack() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b[=65534u").expect("replace and mask flags");
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[30]);
    output(&mut terminal, 1, b"\x1b[=1;2u").expect("OR flags");
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[31]);
    output(&mut terminal, 2, b"\x1b[=5;3u").expect("AND-NOT flags");
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[26]);
    output(&mut terminal, 3, b"\x1b[=9;u").expect("default replace mode");
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[9]);

    output(&mut terminal, 4, b"\x1b[>u").expect("default zero push");
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[9, 0]);
    output(&mut terminal, 5, b"\x1b[<0u").expect("explicit zero pop");
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[9, 0]);
    output(&mut terminal, 6, b"\x1b[<u").expect("default one pop");
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[9]);

    for (sequence, flags) in (1_u8..=9).enumerate() {
        let command = format!("\x1b[>{flags}u");
        output(&mut terminal, 7 + sequence as u64, command.as_bytes()).expect("bounded push");
    }
    assert_eq!(
        terminal.main_keyboard_protocol.as_slice(),
        &[2, 3, 4, 5, 6, 7, 8, 9]
    );
    output(&mut terminal, 16, b"\x1b[=31u").expect("set changes only top");
    assert_eq!(
        terminal.main_keyboard_protocol.as_slice(),
        &[2, 3, 4, 5, 6, 7, 8, 31]
    );
    output(&mut terminal, 17, b"\x1b[<99u").expect("oversized pop empties");
    assert!(terminal.main_keyboard_protocol.is_empty());
    assert_eq!(terminal.main_keyboard_protocol.flags(), 0);
}

#[test]
fn kitty_keyboard_protocol_is_screen_local_and_only_ris_clears_both_stacks() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b[=1u\x1b[>2u").expect("main stack");
    output(&mut terminal, 1, b"\x1b[?1049h\x1b[=4u\x1b[>8u").expect("alternate stack");
    assert!(terminal.is_alternate_screen_active());
    assert_eq!(terminal.main_keyboard_protocol.as_slice(), &[1, 2]);
    assert_eq!(terminal.alternate_keyboard_protocol.as_slice(), &[4, 8]);

    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 2,
            bytes: b"\x1b[?u\x1b[?1049l\x1b[?u",
        })
        .expect("query each active screen");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v2()),
        [b"\x1b[?8u".as_slice(), b"\x1b[?2u".as_slice()]
    );
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("commit queries and switch");
    assert!(!terminal.is_alternate_screen_active());
    assert_eq!(terminal.alternate_keyboard_protocol.as_slice(), &[4, 8]);

    output(&mut terminal, 3, b"\x1bc").expect("RIS");
    assert!(terminal.main_keyboard_protocol.is_empty());
    assert!(terminal.alternate_keyboard_protocol.is_empty());
}

#[test]
fn kitty_query_effects_have_exact_wires_offsets_and_every_split_is_equivalent() {
    let bytes = b"\x1b[=65534u\x1b[?u";
    let mut expected = Terminal::new(4, 2).expect("terminal");
    output(&mut expected, 0, bytes).expect("joined negotiation");
    output(&mut expected, 1, b"").expect("matching event count");

    for split in 0..=bytes.len() {
        let mut terminal = Terminal::new(4, 2).expect("terminal");
        let prepared = terminal
            .prepare_events_atomic_with_host_effects_v2(&[
                Event::Output {
                    source_sequence: 0,
                    bytes: &bytes[..split],
                },
                Event::Output {
                    source_sequence: 1,
                    bytes: &bytes[split..],
                },
            ])
            .expect("split negotiation");
        let frame = prepared.host_effect_frame_v2().to_vec();
        assert_eq!(host_effect_payloads(&frame), [b"\x1b[?30u".as_slice()]);
        assert_eq!(u32_at(&frame, 16), 1);
        assert_eq!(frame.len(), 64 + 40 + b"\x1b[?30u".len());
        let record = 64;
        let (expected_sequence, expected_offset) = if split == bytes.len() {
            (0, bytes.len())
        } else {
            (1, bytes.len() - split)
        };
        assert_eq!(u64_at(&frame, record + 8), expected_sequence);
        assert_eq!(u32_at(&frame, record + 16), expected_offset as u32);
        prepared
            .commit_with(|_| Ok::<(), ()>(()))
            .expect("commit split negotiation");
        assert_eq!(terminal, expected, "split at byte {split}");
    }

    let mut v1 = Terminal::new(4, 2).expect("terminal");
    let before = v1.clone();
    assert!(matches!(
        v1.prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 0,
            bytes,
        }),
        Err(TerminalError::HostEffectVersionRequired)
    ));
    assert_eq!(v1, before);
    assert!(!v1.is_failed_closed());

    let mut legacy = Terminal::new(4, 2).expect("terminal");
    output(&mut legacy, 0, b"\x1b[=7u\x1b[?u").expect("legacy discard query");
    assert_eq!(legacy.main_keyboard_protocol.as_slice(), &[7]);
    assert_eq!(legacy.next_source_sequence(), 1);
}

#[test]
fn malformed_kitty_keyboard_sequences_are_atomic_and_fail_closed() {
    for malformed in [
        b"\x1b[=u".as_slice(),
        b"\x1b[=;1u",
        b"\x1b[=1;0u",
        b"\x1b[=1;4u",
        b"\x1b[=1;1;2u",
        b"\x1b[=1:2u",
        b"\x1b[?0u",
        b"\x1b[?1;2u",
        b"\x1b[>1;2u",
        b"\x1b[>1:2u",
        b"\x1b[<1;2u",
        b"\x1b[<1:2u",
    ] {
        let mut terminal = Terminal::new(4, 2).expect("terminal");
        output(&mut terminal, 0, b"\x1b[=3u\x1b[>4u").expect("baseline stack");
        let before = terminal.clone();
        assert!(matches!(
            output(&mut terminal, 1, malformed),
            Err(TerminalError::Unsupported(_))
        ));
        let mut expected = before;
        expected.failed_closed = true;
        assert_eq!(terminal, expected, "malformed {malformed:?}");
    }

    let mut terminal = Terminal::new(4, 2).expect("terminal");
    let before = terminal.clone();
    assert!(matches!(
        output(&mut terminal, 0, b"\x1b[=1u\x1b[>2u\x1b[=3;9u"),
        Err(TerminalError::Unsupported(_))
    ));
    let mut expected = before;
    expected.failed_closed = true;
    assert_eq!(terminal, expected);
}

#[test]
fn cpr_and_decxcpr_are_margin_relative_under_origin_mode() {
    let mut terminal = Terminal::new(8, 5).expect("terminal");
    output(&mut terminal, 0, b"\x1b[2;4r\x1b[?6h\x1b[2;3H")
        .expect("install margins and relative cursor");
    let prepared = terminal
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 1,
            bytes: b"\x1b[6n\x1b[?6n",
        })
        .expect("relative reports");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v1()),
        [b"\x1b[2;3R".as_slice(), b"\x1b[?2;3R".as_slice()]
    );
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("commit relative reports");

    output(&mut terminal, 2, b"\x1b[?69h\x1b[3;7s\x1b[?6h\x1b[2;4H")
        .expect("activate left-right relative addressing");
    let prepared = terminal
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 3,
            bytes: b"\x1b[?6n",
        })
        .expect("future left-right relative report");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v1()),
        [b"\x1b[?2;4R".as_slice()]
    );
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("commit left-right report");

    terminal.margins.top = 2;
    terminal.margins.bottom = 5;
    terminal.active_screen_mut().position_cursor(0, 0);
    let prepared = terminal
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 4,
            bytes: b"\x1b[?6n",
        })
        .expect("stale cursor outside changed margins is clamped for reporting");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v1()),
        [b"\x1b[?1;1R".as_slice()]
    );
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("commit clamped report");
    assert_eq!(terminal.cursor(), (0, 0));

    let mut maximum = Terminal::new(512, 512).expect("maximum terminal");
    output(&mut maximum, 0, b"\x1b[512;512H").expect("maximum cursor");
    let prepared = maximum
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 1,
            bytes: b"\x1b[6n\x1b[?6n",
        })
        .expect("maximum reports");
    assert_eq!(
        host_effect_payloads(prepared.host_effect_frame_v1()),
        [b"\x1b[512;512R".as_slice(), b"\x1b[?512;512R".as_slice()]
    );
}

#[test]
fn publisher_failure_leaves_every_terminal_field_uncommitted() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"base").expect("baseline");
    let before = terminal.clone();
    let prepared = terminal
        .prepare_events_atomic_with_host_effects_v1(&[
            Event::Output {
                source_sequence: 1,
                bytes: b"\x1b[?2026h\x1b[candidate\x1b[6n",
            },
            Event::Advance {
                source_sequence: 2,
                micros: 17,
            },
        ])
        .expect("prepared candidate");
    let expected_frame = prepared.host_effect_frame_v1().to_vec();
    let mut published = None;
    assert_eq!(
        prepared.commit_with(|owned| {
            published = Some(owned);
            Err("publisher unavailable")
        }),
        Err("publisher unavailable")
    );
    assert_eq!(
        published.expect("publisher received ownership"),
        expected_frame
    );
    assert_eq!(terminal, before);
}

#[test]
fn capture_failures_roll_back_candidate_and_keep_existing_poison_semantics() {
    let mut retryable = Terminal::new(8, 2).expect("terminal");
    let before = retryable.clone();
    assert!(matches!(
        retryable.prepare_events_atomic_with_host_effects_v1(&[
            Event::Output {
                source_sequence: 0,
                bytes: b"\x1b[candidate\x1b[c",
            },
            Event::Resize {
                source_sequence: 1,
                columns: 0,
                rows: 2,
            },
        ]),
        Err(TerminalError::InvalidDimensions)
    ));
    assert_eq!(retryable, before);

    let mut poisoned = Terminal::new(8, 2).expect("terminal");
    output(&mut poisoned, 0, b"\x1b[?2026h").expect("baseline synchronized state");
    let mut expected = poisoned.clone();
    expected.failed_closed = true;
    assert!(matches!(
        poisoned.prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 1,
            bytes: b"\x1b[c\x1b[999m",
        }),
        Err(TerminalError::Unsupported("SGR parameter"))
    ));
    assert_eq!(poisoned, expected);
}

#[test]
fn capture_limits_are_additive_and_do_not_restrict_legacy_discard_apply() {
    let many_events: Vec<_> = (0..=MAX_CAPTURE_EVENT_COUNT)
        .map(|sequence| Event::Advance {
            source_sequence: u64::try_from(sequence).expect("test sequence"),
            micros: 0,
        })
        .collect();
    let mut legacy = Terminal::new(2, 1).expect("terminal");
    legacy
        .apply_events_atomic(&many_events)
        .expect("legacy API has no capture count limit");
    assert_eq!(
        legacy.next_source_sequence(),
        u64::try_from(many_events.len()).expect("event count")
    );

    let mut captured = Terminal::new(2, 1).expect("terminal");
    let before = captured.clone();
    assert!(matches!(
        captured.prepare_events_atomic_with_host_effects_v1(&many_events),
        Err(TerminalError::LimitExceeded)
    ));
    assert_eq!(captured, before);

    let megabyte = vec![b'x'; 1024 * 1024];
    let output_events: Vec<_> = (0_u64..5)
        .map(|source_sequence| Event::Output {
            source_sequence,
            bytes: &megabyte,
        })
        .collect();
    assert!(matches!(
        captured.prepare_events_atomic_with_host_effects_v1(&output_events),
        Err(TerminalError::LimitExceeded)
    ));
    assert_eq!(captured, before);
}

#[test]
fn legacy_queries_discard_effects_and_capture_excludes_osc_and_xtqmodkeys() {
    let mut legacy = Terminal::new(4, 2).expect("terminal");
    output(
        &mut legacy,
        0,
        b"\x1b[c\x1b[>c\x1b[5n\x1b[6n\x1b[?5n\x1b[?6n",
    )
    .expect("legacy reply queries remain accepted");
    assert_eq!(legacy.next_source_sequence(), 1);

    let mut terminal = Terminal::new(4, 2).expect("terminal");
    let prepared = terminal
        .prepare_event_with_host_effects_v1(Event::Output {
            source_sequence: 0,
            bytes: b"\x1b]2;title\x07\x1b[?4m",
        })
        .expect("non-effect host interactions");
    assert_eq!(u32_at(prepared.host_effect_frame_v1(), 16), 0);
    assert_eq!(prepared.host_effect_frame_v1().len(), 64);
    prepared
        .commit_with(|_| Ok::<(), ()>(()))
        .expect("commit non-effects");
    assert_eq!(terminal.osc_state().window_title(), "title");
}

#[test]
fn da_and_status_queries_reject_noncanonical_parameters_fail_closed() {
    for bytes in [
        b"\x1b[1c".as_slice(),
        b"\x1b[>1c".as_slice(),
        b"\x1b[n".as_slice(),
        b"\x1b[0n".as_slice(),
        b"\x1b[7n".as_slice(),
        b"\x1b[5;6n".as_slice(),
        b"\x1b[?0n".as_slice(),
        b"\x1b[?7n".as_slice(),
        b"\x1b[?5;6n".as_slice(),
    ] {
        let mut terminal = Terminal::new(4, 2).expect("terminal");
        assert!(matches!(
            terminal.prepare_event_with_host_effects_v1(Event::Output {
                source_sequence: 0,
                bytes,
            }),
            Err(TerminalError::Unsupported(_))
        ));
        assert!(terminal.is_failed_closed(), "{bytes:?}");
        assert_eq!(terminal.next_source_sequence(), 0);
    }
}

#[test]
fn malformed_incompatible_and_wrong_width_profile_snapshots_are_rejected() {
    let terminal = Terminal::new(4, 2).expect("terminal");
    let snapshot = terminal.export_snapshot_v2().expect("snapshot");

    let mut bad_magic = snapshot.clone();
    bad_magic[0] ^= 1;
    assert_eq!(
        Terminal::import_snapshot_v2(&bad_magic),
        Err(TerminalError::IncompatibleSnapshot)
    );
    let mut profile = snapshot.clone();
    profile[10] = 2;
    assert_eq!(
        Terminal::import_snapshot_v2(&profile),
        Err(TerminalError::IncompatibleSnapshot)
    );
    let mut trailing = snapshot.clone();
    trailing.push(0);
    assert_eq!(
        Terminal::import_snapshot_v2(&trailing),
        Err(TerminalError::MalformedSnapshot)
    );
    assert_eq!(
        Terminal::import_snapshot_v2(&snapshot[..snapshot.len() - 1]),
        Err(TerminalError::MalformedSnapshot)
    );
}

#[test]
fn snapshot_v2_rejects_reserved_state_bits_invalid_enums_and_margins() {
    let terminal = Terminal::new(4, 2).expect("terminal");
    let snapshot = terminal.export_snapshot_v2().expect("snapshot");
    let mode = snapshot::section_payload_offset_for_test(&snapshot, 1);
    let margins = snapshot::section_payload_offset_for_test(&snapshot, 2);
    let charset = snapshot::section_payload_offset_for_test(&snapshot, 4);
    let main_meta = snapshot::section_payload_offset_for_test(&snapshot, 8);

    assert_snapshot_mutation_is_malformed(&snapshot, "unknown mode namespace", |bytes| {
        bytes[mode + 10] = u8::MAX;
    });
    assert_snapshot_mutation_is_malformed(
        &snapshot,
        "non-zero mode record reserved bits",
        |bytes| {
            bytes[mode + 12] = 1;
        },
    );
    assert_snapshot_mutation_is_malformed(&snapshot, "top margin equals bottom margin", |bytes| {
        let bottom: [u8; 4] = bytes[margins + 4..margins + 8]
            .try_into()
            .expect("four-byte bottom margin");
        bytes[margins..margins + 4].copy_from_slice(&bottom);
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "left margin equals right margin", |bytes| {
        let right: [u8; 4] = bytes[margins + 12..margins + 16]
            .try_into()
            .expect("four-byte right margin");
        bytes[margins + 8..margins + 12].copy_from_slice(&right);
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "right margin exceeds columns", |bytes| {
        bytes[margins + 12..margins + 16].copy_from_slice(&5_u32.to_le_bytes());
        bytes[margins + 16..margins + 20].copy_from_slice(&1_u32.to_le_bytes());
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "unknown margin flags", |bytes| {
        bytes[margins + 16..margins + 20].copy_from_slice(&7_u32.to_le_bytes());
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "non-zero margin reserved word", |bytes| {
        bytes[margins + 20] = 1;
    });
    assert_snapshot_mutation_is_malformed(
        &snapshot,
        "disabled DECLRMM with non-canonical full-width intent",
        |bytes| {
            let flags = u32_at(bytes, margins + 16) & !2;
            bytes[margins + 16..margins + 20].copy_from_slice(&flags.to_le_bytes());
        },
    );

    let mut custom = Terminal::new(4, 2).expect("custom margin terminal");
    output(&mut custom, 0, b"\x1b[?69h\x1b[2;3s").expect("custom horizontal margins");
    let custom_snapshot = custom.export_snapshot_v2().expect("custom snapshot");
    let custom_mode = snapshot::section_payload_offset_for_test(&custom_snapshot, 1);
    let custom_margins = snapshot::section_payload_offset_for_test(&custom_snapshot, 2);
    assert_snapshot_mutation_is_malformed(
        &custom_snapshot,
        "custom coordinates marked as following screen width",
        |bytes| {
            let flags = u32_at(bytes, custom_margins + 16) | 2;
            bytes[custom_margins + 16..custom_margins + 20].copy_from_slice(&flags.to_le_bytes());
        },
    );
    assert_snapshot_mutation_is_malformed(
        &custom_snapshot,
        "custom horizontal margin narrowed to one column",
        |bytes| {
            bytes[custom_margins + 12..custom_margins + 16].copy_from_slice(&2_u32.to_le_bytes());
        },
    );
    assert_snapshot_mutation_is_malformed(
        &custom_snapshot,
        "custom margins without DECLRMM",
        |bytes| {
            let count = usize::try_from(u32_at(bytes, custom_mode)).expect("mode record count");
            let record = (0..count)
                .map(|index| custom_mode + 8 + index * 8)
                .find(|&offset| {
                    u16::from_le_bytes(
                        bytes[offset..offset + 2]
                            .try_into()
                            .expect("mode parameter"),
                    ) == 69
                })
                .expect("DECLRMM record");
            bytes[record..record + 2].copy_from_slice(&1004_u16.to_le_bytes());
        },
    );
    assert_snapshot_mutation_is_malformed(&snapshot, "reserved saved-cursor mode bit", |bytes| {
        bytes[main_meta + 40] |= 0x10;
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "unknown charset designator", |bytes| {
        bytes[charset + 8] = 3;
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "invalid charset GL register", |bytes| {
        bytes[charset + 12] = 4;
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "invalid charset GR register", |bytes| {
        bytes[charset + 13] = 4;
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "invalid charset single shift", |bytes| {
        bytes[charset + 14] = 1;
    });
    assert_snapshot_mutation_is_malformed(&snapshot, "non-zero charset reserved byte", |bytes| {
        bytes[charset + 15] = 1;
    });
}

#[test]
fn osc_hyperlink_open_print_close_print_binds_only_open_cells() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b]8;id=primary;https://example.test/path\x07A\x1b]8;;\x1b\\B",
    )
    .expect("open, print, close, and print");

    let linked = terminal.cell(0, 0).expect("linked cell");
    let link_ref = linked.hyperlink.expect("open OSC 8 binds the cell");
    assert_eq!(terminal.cell(0, 1).expect("plain cell").hyperlink, None);
    assert_eq!(terminal.osc_state().current_hyperlink(), None);
    assert_eq!(terminal.osc_state().hyperlink_count(), 1);
    let link = terminal
        .osc_state()
        .hyperlink(link_ref)
        .expect("stable hyperlink table entry");
    assert_eq!(link.id(), Some("primary"));
    assert_eq!(link.uri(), "https://example.test/path");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn hyperlink_cells_survive_sgr_cursor_edits_alternate_and_reflow() {
    let mut edited = Terminal::new(8, 2).expect("terminal");
    output(
        &mut edited,
        0,
        b"\x1b]8;;edit-link\x07\x1b[1mA\x1b[0mB\x1b7C\x1b8D\x1b[1;2H\x1b[@\x1b[1;3H\x1b[X\x1b]8;;\x07E",
    )
    .expect("linked rendition, cursor save/restore, insert, and erase");
    let link_ref = edited
        .cell(0, 0)
        .expect("bold linked cell")
        .hyperlink
        .expect("linked A");
    assert!(edited.cell(0, 0).expect("A").attributes.bold);
    assert_eq!(edited.cell(0, 1).expect("inserted blank").hyperlink, None);
    assert_eq!(edited.cell(0, 2).expect("post-close E").character, 'E');
    assert_eq!(edited.cell(0, 2).expect("post-close E").hyperlink, None);
    assert_eq!(edited.cell(0, 3).expect("restored D").character, 'D');
    assert_eq!(
        edited.cell(0, 3).expect("restored D").hyperlink,
        Some(link_ref)
    );
    assert_eq!(
        edited.cell(0, 3).expect("SGR 0 D").attributes,
        Attributes::default()
    );

    let mut screens = Terminal::new(6, 2).expect("terminal");
    output(&mut screens, 0, b"\x1b]8;;screen-link\x07P").expect("linked primary");
    let screen_link = screens.cell(0, 0).expect("primary P").hyperlink;
    output(&mut screens, 1, b"\x1b[?1049hA").expect("linked alternate");
    assert_eq!(
        screens.cell(0, 0).expect("alternate A").hyperlink,
        screen_link
    );
    output(&mut screens, 2, b"\x1b[?1049lM\x1b]8;;\x07N")
        .expect("return, print linked, then close");
    assert_eq!(
        screens.cell(0, 0).expect("primary P").hyperlink,
        screen_link
    );
    assert_eq!(
        screens.cell(0, 1).expect("primary M").hyperlink,
        screen_link
    );
    assert_eq!(screens.cell(0, 2).expect("primary N").hyperlink, None);
    assert_eq!(screens.alternate.cells[0], Cell::default());

    let mut reflowed = Terminal::new(4, 3).expect("terminal");
    output(
        &mut reflowed,
        0,
        b"\x1b]8;;reflow-link\x1b\\abcdef\x1b]8;;\x07",
    )
    .expect("wrapped linked text");
    let reflow_link = reflowed.cell(0, 0).expect("linked a").hyperlink;
    reflowed
        .apply_event(Event::Resize {
            source_sequence: 1,
            columns: 3,
            rows: 3,
        })
        .expect("linked primary reflow");
    assert_eq!(row_text(&reflowed, 0), "abc");
    assert_eq!(row_text(&reflowed, 1), "def");
    for row in 0..2 {
        for column in 0..3 {
            assert_eq!(
                reflowed
                    .cell(row, column)
                    .expect("reflowed linked cell")
                    .hyperlink,
                reflow_link
            );
        }
    }
    assert_snapshot_self_closure(&reflowed);
}

#[test]
fn sgr_zero_preserves_decsca_protection_and_open_hyperlink() {
    let mut terminal = Terminal::new(4, 1).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b]8;;sgr-zero-link\x07\x1b[1\"q\x1b[31;5mP\x1b[0mU\x1b[1;1;1;2${V",
    )
    .expect("protected rendition reset and selective erase");

    let protected = terminal.cell(0, 0).expect("protected P");
    assert_eq!(protected.character, 'P');
    assert!(protected.protected);
    assert_eq!(protected.attributes.foreground, Color::Indexed(1));
    assert!(protected.attributes.blink);

    let after_reset = terminal.cell(0, 1).expect("protected U after SGR 0");
    assert_eq!(after_reset.character, 'U');
    assert!(after_reset.protected);
    assert_eq!(after_reset.attributes, Attributes::default());

    let linked = protected.hyperlink.expect("open link on P");
    assert_eq!(after_reset.hyperlink, Some(linked));
    assert_eq!(
        terminal.cell(0, 2).expect("linked V").hyperlink,
        Some(linked)
    );
    assert_eq!(terminal.osc_state().current_hyperlink(), Some(linked));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn alternate_screen_inherits_rendition_protection_and_open_hyperlink() {
    let mut terminal = Terminal::new(6, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b]8;;alternate-link\x07\x1b[31;5m\x1b[1\"qM\x1b[?1049hA\x1b7\x1b[?1049hB",
    )
    .expect("enter alternate twice");

    assert!(terminal.is_alternate_screen_active());
    assert_eq!(row_text(&terminal, 0), "AB    ");
    assert_eq!(terminal.alternate.cursor_row, 0);
    assert_eq!(terminal.alternate.cursor_column, 2);
    assert_eq!(terminal.alternate.saved.column, 1);
    let link = terminal.alternate.cells[0]
        .hyperlink
        .expect("open link inherited by alternate output");
    for cell in &terminal.alternate.cells[..2] {
        assert_eq!(cell.attributes.foreground, Color::Indexed(1));
        assert!(cell.attributes.blink);
        assert!(cell.protected);
        assert_eq!(cell.hyperlink, Some(link));
    }

    output(&mut terminal, 1, b"\x1b[0m\x1b[2\"q\x1b[?1049lN")
        .expect("restore primary cursor and rendition");
    assert!(!terminal.is_alternate_screen_active());
    assert_eq!(row_text(&terminal, 0), "MN    ");
    let restored = terminal.cell(0, 1).expect("restored primary rendition");
    assert_eq!(restored.attributes.foreground, Color::Indexed(1));
    assert!(restored.attributes.blink);
    assert!(restored.protected);
    assert_eq!(restored.hyperlink, Some(link));

    output(&mut terminal, 2, b"\x1b[?1049hC").expect("re-enter a fresh alternate screen");
    assert_eq!(row_text(&terminal, 0), "C     ");
    assert_eq!(terminal.alternate.cursor_row, 0);
    assert_eq!(terminal.alternate.cursor_column, 1);
    assert_eq!(terminal.alternate.saved, screen::SavedCursor::default());
    let fresh = terminal.cell(0, 0).expect("fresh alternate rendition");
    assert_eq!(fresh.attributes.foreground, Color::Indexed(1));
    assert!(fresh.attributes.blink);
    assert!(fresh.protected);
    assert_eq!(fresh.hyperlink, Some(link));
    assert_eq!(terminal.osc_state().current_hyperlink(), Some(link));
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn osc_title_palette_and_open_link_snapshot_replay_an_equivalent_suffix() {
    let mut direct = Terminal::new(8, 2).expect("terminal");
    output(
        &mut direct,
        0,
        b"\x1b]0;base\x07\x1b[22;0t\x1b]2;top\x07\x1b]4;7;#123456\x1b\\\x1b]8;id=snapshot;https://example.test\x07A",
    )
    .expect("persistent OSC checkpoint state");
    let snapshot = direct.export_snapshot_v2().expect("OSC checkpoint");
    let mut restored = Terminal::import_snapshot_v2(&snapshot).expect("OSC restore");
    assert_eq!(restored, direct);
    assert!(restored.osc_state().current_hyperlink().is_some());

    let suffix = b"\x1b[23;0tB\x1b]8;;\x07C";
    output(&mut direct, 1, suffix).expect("direct OSC suffix");
    output(&mut restored, 1, suffix).expect("restored OSC suffix");
    assert_eq!(restored, direct);
    assert_eq!(restored.osc_state().icon_title(), "base");
    assert_eq!(restored.osc_state().window_title(), "base");
    assert_eq!(
        restored.osc_state().palette(7),
        Some(OscColor::new(0x12, 0x34, 0x56))
    );
    let link_ref = restored.cell(0, 0).expect("checkpoint A").hyperlink;
    assert!(link_ref.is_some());
    assert_eq!(restored.cell(0, 1).expect("suffix B").hyperlink, link_ref);
    assert_eq!(restored.cell(0, 2).expect("closed C").hyperlink, None);
    assert_eq!(
        restored
            .export_snapshot_v2()
            .expect("restored final snapshot"),
        direct.export_snapshot_v2().expect("direct final snapshot")
    );
}

#[test]
fn csi_title_stack_sequences_split_across_output_events() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b]2;base\x07").expect("base title");
    output(&mut terminal, 1, b"\x1b[22;2").expect("split save prefix");
    assert_eq!(
        terminal.export_snapshot_v2(),
        Err(TerminalError::SnapshotNotEligible)
    );
    output(&mut terminal, 2, b"t").expect("split save suffix");
    output(&mut terminal, 3, b"\x1b]2;temporary\x07").expect("temporary title");
    output(&mut terminal, 4, b"\x1b[23;2").expect("split restore prefix");
    assert_eq!(
        terminal.export_snapshot_v2(),
        Err(TerminalError::SnapshotNotEligible)
    );
    output(&mut terminal, 5, b"t").expect("split restore suffix");
    assert_eq!(terminal.osc_state().window_title(), "base");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn csi_window_title_stack_accepts_the_conpty_compatibility_form() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b]1;icon-base\x07\x1b]2;window-base\x07\x1b[22;0;0t",
    )
    .expect("save both titles");
    output(
        &mut terminal,
        1,
        b"\x1b]1;icon-current\x07\x1b]2;window-current\x07\x1b[23;0;0t",
    )
    .expect("restore both titles");

    assert_eq!(terminal.osc_state().icon_title(), "icon-base");
    assert_eq!(terminal.osc_state().window_title(), "window-base");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn csi_window_title_stack_validates_targets_and_other_operations_fail_closed() {
    for (name, bytes, expected) in [
        (
            "invalid target",
            b"\x1b[22;3;0t".as_slice(),
            TerminalError::Unsupported("window title target"),
        ),
        (
            "invalid direct slot",
            b"\x1b[22;0;11t".as_slice(),
            TerminalError::Unsupported("window title stack slot"),
        ),
        (
            "extra parameter",
            b"\x1b[22;0;0;0t".as_slice(),
            TerminalError::Unsupported("window manipulation parameters"),
        ),
        (
            "unimplemented operation",
            b"\x1b[8;24;80t".as_slice(),
            TerminalError::Unsupported("window manipulation"),
        ),
    ] {
        let mut terminal = Terminal::new(8, 2).expect("terminal");
        output(&mut terminal, 0, b"stable").expect("baseline");
        let before = terminal.clone();

        assert_eq!(output(&mut terminal, 1, bytes), Err(expected), "{name}");
        let mut expected_state = before;
        expected_state.failed_closed = true;
        assert_eq!(terminal, expected_state, "{name}");
    }
}

#[test]
fn malformed_osc_rolls_back_the_whole_output_then_fails_closed() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b]2;old\x07Q").expect("baseline state");
    let before = terminal.clone();

    assert_eq!(
        output(&mut terminal, 1, b"\x1b]2;new\x07X\x1b]4;1;not-a-color\x07"),
        Err(TerminalError::Unsupported("malformed OSC state"))
    );
    let mut expected = before;
    expected.failed_closed = true;
    assert_eq!(terminal, expected);
    assert_eq!(terminal.osc_state().window_title(), "old");
    assert_eq!(row_text(&terminal, 0), "Q       ");
    assert_eq!(terminal.next_source_sequence(), 1);
    assert_eq!(
        output(&mut terminal, 1, b"retry"),
        Err(TerminalError::FailedClosed)
    );
}

#[test]
fn ris_resets_osc_state_and_removes_linked_presentation() {
    let mut terminal = Terminal::new(8, 2).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b]0;title\x07\x1b]4;3;#abcdef\x07\x1b]8;;ris-link\x07\x1b[1mX",
    )
    .expect("non-default OSC and presentation state");
    assert!(terminal.cell(0, 0).expect("linked X").hyperlink.is_some());
    output(&mut terminal, 1, b"\x1bc").expect("RIS");

    assert_eq!(terminal.osc_state(), &OscState::default());
    assert_eq!(row_text(&terminal, 0), "        ");
    assert_eq!(terminal.cell(0, 0).expect("reset blank").hyperlink, None);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn dec_rectangle_coordinates_default_zero_clip_and_ignore_inversion() {
    let fill = b"AAAAA\x1b[2;1HBBBBB\x1b[3;1HCCCCC";

    for erase in [&b"\x1b[$z"[..], &b"\x1b[0;0;0;0$z"[..]] {
        let mut terminal = Terminal::new(5, 3).expect("terminal");
        output(&mut terminal, 0, fill).expect("filled grid");
        output(&mut terminal, 1, erase).expect("defaulted DECERA");
        for row in 0..3 {
            assert_eq!(row_text(&terminal, row), "     ");
        }
    }

    let mut bounded = Terminal::new(5, 3).expect("terminal");
    output(&mut bounded, 0, fill).expect("filled grid");
    output(&mut bounded, 1, b"\x1b[3;5;2;1$z").expect("inverted DECERA is ignored");
    assert_eq!(row_text(&bounded, 0), "AAAAA");
    assert_eq!(row_text(&bounded, 1), "BBBBB");
    assert_eq!(row_text(&bounded, 2), "CCCCC");
    output(&mut bounded, 2, b"\x1b[999;999;999;999$z").expect("clipped DECERA");
    assert_eq!(row_text(&bounded, 0), "AAAAA");
    assert_eq!(row_text(&bounded, 1), "BBBBB");
    assert_eq!(row_text(&bounded, 2), "CCCC ");
    assert_snapshot_self_closure(&bounded);
}

#[test]
fn dec_rectangles_are_relative_to_decom_vertical_margins() {
    let mut terminal = Terminal::new(6, 5).expect("terminal");
    output(
        &mut terminal,
        0,
        b"000000\x1b[2;1H111111\x1b[3;1H222222\x1b[4;1H333333\x1b[5;1H444444",
    )
    .expect("filled grid");
    output(&mut terminal, 1, b"\x1b[2;4r\x1b[?6h\x1b[$z").expect("origin-relative DECERA");

    assert_eq!(row_text(&terminal, 0), "000000");
    for row in 1..4 {
        assert_eq!(row_text(&terminal, row), "      ");
    }
    assert_eq!(row_text(&terminal, 4), "444444");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn deccra_copies_clips_and_handles_overlap_from_a_staged_source() {
    let baseline = b"ABC\r\nDEF\r\nGHI";

    let mut offset = Terminal::new(7, 3).expect("terminal");
    output(&mut offset, 0, baseline).expect("source grid");
    let cursor = offset.cursor();
    output(&mut offset, 1, b"\x1b[1;1;2;2;1;2;5;1$v").expect("offset DECCRA");
    assert_eq!(row_text(&offset, 0), "ABC    ");
    assert_eq!(row_text(&offset, 1), "DEF AB ");
    assert_eq!(row_text(&offset, 2), "GHI DE ");
    assert_eq!(offset.cursor(), cursor);

    let mut overlap = Terminal::new(7, 3).expect("terminal");
    output(&mut overlap, 0, baseline).expect("overlap source grid");
    output(&mut overlap, 1, b"\x1b[1;1;3;3;1;1;3;1$v").expect("right-overlapping DECCRA");
    assert_eq!(row_text(&overlap, 0), "ABABC  ");
    assert_eq!(row_text(&overlap, 1), "DEDEF  ");
    assert_eq!(row_text(&overlap, 2), "GHGHI  ");

    let mut clipped = Terminal::new(7, 3).expect("terminal");
    output(&mut clipped, 0, b"ABCDEFG\x1b[2;1HHIJKLMN\x1b[3;1HOPQRSTU")
        .expect("clipping source grid");
    output(&mut clipped, 1, b"\x1b[2;2;999;999;1;3;6;1$v")
        .expect("source and destination clipping");
    assert_eq!(row_text(&clipped, 0), "ABCDEFG");
    assert_eq!(row_text(&clipped, 1), "HIJKLMN");
    assert_eq!(row_text(&clipped, 2), "OPQRSIJ");
    output(&mut clipped, 2, b"\x1b[3;5;2;1;1;1;1;1$v")
        .expect("inverted source rectangle is ignored");
    assert_eq!(row_text(&clipped, 0), "ABCDEFG");
    assert_eq!(row_text(&clipped, 1), "HIJKLMN");
    assert_eq!(row_text(&clipped, 2), "OPQRSIJ");
    output(&mut clipped, 3, b"\x1b[0;0;0;0;0;0;0;0$v")
        .expect("zero-valued DECCRA defaults to the current full page");
    assert_eq!(row_text(&clipped, 0), "ABCDEFG");
    assert_eq!(row_text(&clipped, 1), "HIJKLMN");
    assert_eq!(row_text(&clipped, 2), "OPQRSIJ");
    assert_snapshot_self_closure(&clipped);
}

#[test]
fn deccra_coordinates_follow_decom_and_declrmm_and_pages_are_strict() {
    let mut terminal = Terminal::new(5, 5).expect("terminal");
    output(
        &mut terminal,
        0,
        b"AAAAA\x1b[2;1HBBBBB\x1b[3;1HCCCCC\x1b[4;1HDDDDD\x1b[5;1HEEEEE",
    )
    .expect("filled page");
    output(
        &mut terminal,
        1,
        b"\x1b[2;4r\x1b[?69h\x1b[2;4s\x1b[?6h\x1b[1;1;1;2;1;2;2;1$v",
    )
    .expect("origin-relative DECCRA");
    assert_eq!(row_text(&terminal, 0), "AAAAA");
    assert_eq!(row_text(&terminal, 1), "BBBBB");
    assert_eq!(row_text(&terminal, 2), "CCBBC");
    assert_eq!(row_text(&terminal, 3), "DDDDD");
    assert_eq!(row_text(&terminal, 4), "EEEEE");
    assert_snapshot_self_closure(&terminal);

    for bytes in [
        b"\x1b[1;1;1;1;2;2;2;1$v".as_slice(),
        b"\x1b[1;1;1;1;1;2;2;2$v".as_slice(),
    ] {
        let mut invalid_page = Terminal::new(5, 2).expect("terminal");
        output(&mut invalid_page, 0, b"stable").expect("baseline");
        let before = invalid_page.clone();
        assert_eq!(
            output(&mut invalid_page, 1, bytes),
            Err(TerminalError::Unsupported("DECCRA page"))
        );
        let mut expected = before;
        expected.failed_closed = true;
        assert_eq!(invalid_page, expected);
    }
}

#[test]
fn deccra_preserves_cell_presentation_cursor_wrap_and_row_history_metadata() {
    let options = TerminalOptions {
        history_capacity_rows: 2,
    };
    let mut terminal = Terminal::new_with_options(8, 4, 0, options).expect("history terminal");
    output(
        &mut terminal,
        0,
        "\u{1b}]8;;https://copy.test\u{1b}\\\u{1b}[31;1m\u{1b}[1\"qe\u{301}\u{1b}[0\"q\u{1b}[0m\u{1b}]8;;\u{1b}\\\u{1b}[3;8HZ"
            .as_bytes(),
    )
    .expect("styled protected linked source and delayed wrap");
    let source = terminal.cell(0, 0).expect("source grapheme").clone();
    assert_eq!(source.text(), "e\u{301}");
    assert!(source.attributes.bold);
    assert!(source.protected);
    assert!(source.hyperlink.is_some());
    let cursor = terminal.cursor();
    let wrap_pending = terminal.active_screen().wrap_pending;
    let attributes = terminal.active_screen().attributes;
    let protected = terminal.active_screen().protected;
    let saved = terminal.active_screen().saved;
    let soft_wrapped = terminal.main.soft_wrapped.clone();
    let row_metadata = terminal.main.row_metadata.clone();
    let viewport_top = terminal.main.viewport_top();

    output(&mut terminal, 1, b"\x1b[1;1;1;1;1;2;2;1$v").expect("presentation-preserving DECCRA");
    assert_eq!(terminal.cell(1, 1).expect("copied cell"), source);
    assert_eq!(terminal.cursor(), cursor);
    assert_eq!(terminal.active_screen().wrap_pending, wrap_pending);
    assert!(wrap_pending);
    assert_eq!(terminal.active_screen().attributes, attributes);
    assert_eq!(terminal.active_screen().protected, protected);
    assert_eq!(terminal.active_screen().saved, saved);
    assert_eq!(terminal.main.soft_wrapped, soft_wrapped);
    assert_eq!(terminal.main.row_metadata, row_metadata);
    assert_eq!(terminal.main.viewport_top(), viewport_top);

    let snapshot = terminal.export_snapshot_v2().expect("DECCRA snapshot");
    let restored = Terminal::import_snapshot_v2(&snapshot).expect("restore copied presentation");
    assert_eq!(restored, terminal);
    assert_eq!(
        restored
            .export_render_frame_v1()
            .expect("restored V1 frame"),
        terminal.export_render_frame_v1().expect("direct V1 frame")
    );
    assert_eq!(
        restored
            .export_render_frame_v2()
            .expect("restored V2 frame"),
        terminal.export_render_frame_v2().expect("direct V2 frame")
    );
}

#[test]
fn deccra_is_split_deterministic_alternate_safe_and_event_atomic() {
    let command = b"\x1b[1;1;2;2;1;2;5;1$v";
    let baseline = b"ABC\r\nDEF\r\nGHI";
    let mut expected = Terminal::new(7, 3).expect("terminal");
    output(&mut expected, 0, baseline).expect("joined source grid");
    output(&mut expected, 1, command).expect("joined DECCRA");
    output(&mut expected, 2, b"").expect("matching event count");
    for split in 0..=command.len() {
        let mut actual = Terminal::new(7, 3).expect("terminal");
        output(&mut actual, 0, baseline).expect("split source grid");
        output(&mut actual, 1, &command[..split]).expect("DECCRA prefix");
        output(&mut actual, 2, &command[split..]).expect("DECCRA suffix");
        assert_eq!(actual, expected, "split at byte {split}");
    }

    let mut alternate = Terminal::new(7, 3).expect("terminal");
    output(
        &mut alternate,
        0,
        b"MAIN\x1b[?1049hABC\r\nDEF\r\nGHI\x1b[1;1;2;2;1;2;5;1$v",
    )
    .expect("alternate-screen DECCRA");
    assert!(alternate.is_alternate_screen_active());
    assert_eq!(row_text(&alternate, 1), "DEF AB ");
    assert_snapshot_self_closure(&alternate);
    output(&mut alternate, 1, b"\x1b[?1049l").expect("return to primary");
    assert_eq!(row_text(&alternate, 0), "MAIN   ");

    let mut atomic = Terminal::new(7, 3).expect("terminal");
    output(&mut atomic, 0, baseline).expect("atomic source grid");
    let before = atomic.clone();
    assert_eq!(
        output(
            &mut atomic,
            1,
            b"\x1b[1;1;2;2;1;2;5;1$v\x1b[1;1;1;1;2;1;1;1$v",
        ),
        Err(TerminalError::Unsupported("DECCRA page"))
    );
    let mut failed_closed = before;
    failed_closed.failed_closed = true;
    assert_eq!(atomic, failed_closed);

    let mut too_many = Terminal::new(2, 2).expect("terminal");
    assert_eq!(
        output(&mut too_many, 0, b"\x1b[1;1;1;1;1;1;1;1;1$v"),
        Err(TerminalError::Unsupported("DECCRA parameters"))
    );
    assert!(too_many.is_failed_closed());
}

#[test]
fn decfra_classifies_pch_boundaries_and_preserves_pending_single_shift() {
    let mut boundaries = Terminal::new(4, 2).expect("terminal");
    output(
        &mut boundaries,
        0,
        b"....\x1b[2;1HWXYZ\x1b[32;1;1;1;1$x\x1b[126;1;2;1;2$x\x1b[160;1;3;1;3$x\x1b[255;1;4;1;4$x\x1b[31;2;1;2;1$x\x1b[127;2;2;2;2$x\x1b[159;2;3;2;3$x\x1b[256;2;4;2;4$x",
    )
    .expect("valid and ignored DECFRA Pch boundaries");
    assert_eq!(boundaries.cell(0, 0).expect("Pch 32").character, ' ');
    assert_eq!(boundaries.cell(0, 1).expect("Pch 126").character, '~');
    assert_eq!(
        boundaries.cell(0, 2).expect("Pch 160").character,
        '\u{00a0}'
    );
    assert_eq!(
        boundaries.cell(0, 3).expect("Pch 255").character,
        '\u{00ff}'
    );
    assert_eq!(row_text(&boundaries, 1), "WXYZ");

    let mut shifted = Terminal::new(4, 2).expect("terminal");
    output(&mut shifted, 0, b"\x1b*0\x1bN\x1b[113;1;1;1;1$x").expect("DECFRA while SS2 is pending");
    assert_eq!(shifted.cell(0, 0).expect("ASCII DECFRA").character, 'q');
    assert_eq!(
        shifted.charset.pending_single_shift(),
        Some(GraphicRegister::G2)
    );
    output(&mut shifted, 1, b"\x1b[1;2Hq").expect("consume SS2 with a graphic");
    assert_eq!(
        shifted.cell(0, 1).expect("SS2 graphic").character,
        '\u{2500}'
    );
    assert_eq!(shifted.charset.pending_single_shift(), None);
    assert_snapshot_self_closure(&shifted);
}

#[test]
fn decsace_stream_and_rectangle_extents_select_different_cells() {
    let fill = b"abcde\x1b[2;1Hfghij\x1b[3;1Hklmno";
    let mut stream = Terminal::new(5, 3).expect("terminal");
    let mut rectangle = Terminal::new(5, 3).expect("terminal");
    output(&mut stream, 0, fill).expect("stream grid");
    output(&mut rectangle, 0, fill).expect("rectangle grid");
    output(&mut stream, 1, b"\x1b[1;2;3;4;1$r").expect("stream DECCARA");
    output(&mut rectangle, 1, b"\x1b[2*x\x1b[1;2;3;4;1$r").expect("rectangle DECCARA");

    let stream_expected = [
        [false, true, true, true, true],
        [true, true, true, true, true],
        [true, true, true, true, false],
    ];
    let rectangle_expected = [
        [false, true, true, true, false],
        [false, true, true, true, false],
        [false, true, true, true, false],
    ];
    for row in 0..3 {
        for column in 0..5 {
            assert_eq!(
                stream
                    .cell(row, column)
                    .expect("stream cell")
                    .attributes
                    .bold,
                stream_expected[row as usize][column as usize]
            );
            assert_eq!(
                rectangle
                    .cell(row, column)
                    .expect("rectangle cell")
                    .attributes
                    .bold,
                rectangle_expected[row as usize][column as usize]
            );
        }
    }
    assert_snapshot_self_closure(&stream);
    assert_snapshot_self_closure(&rectangle);
}

#[test]
fn rectangle_rendition_parameters_apply_in_order_and_reverse_by_parity() {
    let mut terminal = Terminal::new(2, 1).expect("terminal");
    output(&mut terminal, 0, b"A\x1b[2*x").expect("rectangle extent");
    output(&mut terminal, 1, b"\x1b[1;1;1;1;1;22;4;8;9;28$r").expect("ordered DECCARA parameters");
    let ordered = terminal.cell(0, 0).expect("ordered cell").attributes;
    assert!(!ordered.bold);
    assert!(ordered.is_underlined());
    assert!(!ordered.conceal);
    assert!(ordered.strikethrough);

    output(&mut terminal, 2, b"\x1b[1;1;1;1;1;1;4;4;8;28;9;29$t").expect("even DECRARA toggles");
    assert_eq!(
        terminal.cell(0, 0).expect("even toggles").attributes,
        ordered
    );
    output(&mut terminal, 3, b"\x1b[1;1;1;1;0;4$t").expect("ordered all-then-underline toggle");
    let toggled = terminal.cell(0, 0).expect("toggle result").attributes;
    assert!(toggled.bold);
    assert!(toggled.is_underlined());
    assert!(toggled.blink);
    assert!(toggled.inverse);
    assert!(toggled.conceal);
    assert!(!toggled.strikethrough);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn deccara_applies_the_complete_ordered_sgr_transform_without_other_cell_state_changes() {
    let mut terminal = Terminal::new(4, 1).expect("terminal");
    output(
        &mut terminal,
        0,
        b"\x1b[1;2;3;4:3;5;7;8;9;53mA\x1b[0mB\x1b[1;1H\x1b7\x1b[2*x",
    )
    .expect("rich source and rectangle extent");
    let cursor = terminal.cursor();
    let saved = terminal.active_screen().saved;
    let metadata = terminal.main.row_metadata.clone();

    output(&mut terminal, 1, b"\x1b[1;1;1;1;0$r").expect("DECCARA reset");
    assert_eq!(
        terminal.cell(0, 0).expect("reset A").attributes,
        Attributes::default()
    );
    output(&mut terminal, 2, b"\x1b[1;2;1;2;1;2;3;4:5;6;7;8;9;53$r")
        .expect("DECCARA boolean and underline fields");
    output(
        &mut terminal,
        3,
        b"\x1b[1;2;1;2;38:5:7;48;5;8;58:2::1:2:3$r",
    )
    .expect("DECCARA complete colors");
    let rich = terminal.cell(0, 1).expect("changed B");
    assert_eq!(rich.character, 'B');
    assert!(!rich.protected);
    assert_eq!(rich.hyperlink, None);
    let attributes = rich.attributes;
    assert!(attributes.bold && attributes.faint && attributes.italic);
    assert_eq!(attributes.underline_style, UnderlineStyle::Dashed);
    assert!(
        attributes.blink
            && attributes.inverse
            && attributes.conceal
            && attributes.strikethrough
            && attributes.overline
    );
    assert_eq!(attributes.foreground, Color::Indexed(7));
    assert_eq!(attributes.background, Color::Indexed(8));
    assert_eq!(attributes.underline_color, Color::Rgb(1, 2, 3));
    assert_eq!(terminal.cursor(), cursor);
    assert_eq!(terminal.active_screen().saved, saved);
    assert_eq!(terminal.main.row_metadata, metadata);

    output(
        &mut terminal,
        4,
        b"\x1b[1;2;1;2;22;23;24;25;27;28;29;55;39;49;59$r",
    )
    .expect("DECCARA ordered clear fields");
    assert_eq!(
        terminal.cell(0, 1).expect("cleared B").attributes,
        Attributes::default()
    );
    output(&mut terminal, 5, b"\x1b[1;1;1;1$r").expect("no-tail DECCARA no-op");
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn decrara_extended_toggles_are_enum_safe_involutions_and_colors_are_noops() {
    let mut terminal = Terminal::new(5, 1).expect("terminal");
    output(
        &mut terminal,
        0,
        b"A\x1b[4mB\x1b[21mC\x1b[4:3mD\x1b[0m\x1b[2*x",
    )
    .expect("underline style fixtures");

    output(&mut terminal, 1, b"\x1b[1;1;1;1;0$t").expect("toggle all axes");
    let all = terminal.cell(0, 0).expect("all toggled A").attributes;
    assert!(all.bold && all.faint && all.italic);
    assert_eq!(all.underline_style, UnderlineStyle::Single);
    assert!(all.blink && all.inverse && all.conceal && all.strikethrough && all.overline);

    let before_colors = all;
    output(&mut terminal, 2, b"\x1b[1;1;1;1;30;38:5:9;39;48;5;8;49$t")
        .expect("legal reverse foreground and background selectors are no-ops");
    output(&mut terminal, 3, b"\x1b[1;1;1;1;58:5:7;59$t")
        .expect("legal reverse underline color selectors are no-ops");
    assert_eq!(
        terminal.cell(0, 0).expect("color no-op A").attributes,
        before_colors
    );

    output(&mut terminal, 4, b"\x1b[1;2;1;2;4;24$t").expect("paired single toggles");
    assert_eq!(
        terminal
            .cell(0, 1)
            .expect("single B")
            .attributes
            .underline_style,
        UnderlineStyle::Single
    );
    output(&mut terminal, 5, b"\x1b[1;3;1;3;4$t").expect("other style remains");
    assert_eq!(
        terminal
            .cell(0, 2)
            .expect("double C")
            .attributes
            .underline_style,
        UnderlineStyle::Double
    );
    output(&mut terminal, 6, b"\x1b[1;4;1;4;4:3$t").expect("curly toggles off");
    assert_eq!(
        terminal
            .cell(0, 3)
            .expect("curly D")
            .attributes
            .underline_style,
        UnderlineStyle::None
    );
    output(&mut terminal, 7, b"\x1b[1;4;1;4;4:0$t").expect("4:0 reverse no-op");
    assert_eq!(
        terminal
            .cell(0, 3)
            .expect("still plain D")
            .attributes
            .underline_style,
        UnderlineStyle::None
    );

    let once = terminal.cell(0, 0).expect("once toggled A").attributes;
    output(&mut terminal, 8, b"\x1b[1;1;1;1;1;2;3;5;7;8;9;53;4:2$t")
        .expect("first involution application");
    output(&mut terminal, 9, b"\x1b[1;1;1;1;1;2;3;5;7;8;9;53;4:2$t")
        .expect("second involution application");
    assert_eq!(terminal.cell(0, 0).expect("restored A").attributes, once);
    assert_snapshot_self_closure(&terminal);
}

#[test]
fn rectangle_rendition_malformed_tails_and_coordinate_colons_are_atomic() {
    for bytes in [
        b"\x1b[1;1;1;1;1;4:6$r".as_slice(),
        b"\x1b[1;1;1;1;58;5;$r".as_slice(),
        b"\x1b[1;1;1;1;59:0$t".as_slice(),
        b"\x1b[1:1;1;1;1$r".as_slice(),
        b"\x1b[1;1;1;1:4:3$t".as_slice(),
    ] {
        let mut terminal = Terminal::new(3, 1).expect("terminal");
        output(&mut terminal, 0, b"\x1b[1;2;4:4;53mA").expect("baseline");
        let mut expected = terminal.clone();
        expected.failed_closed = true;
        assert!(matches!(
            output(&mut terminal, 1, bytes),
            Err(TerminalError::Unsupported(_))
        ));
        assert_eq!(
            terminal, expected,
            "malformed rectangle rendition: {bytes:?}"
        );
    }
}

#[test]
fn standard_erase_blanks_keep_colors_but_clear_character_attributes() {
    const RICH: &[u8] = b"\x1b[1;2;3;4:4;5;7;8;9;53;31;44;58:5:6m";

    for (name, command, inspected_column) in [
        ("ED", b"\x1b[2J".as_slice(), 0),
        ("EL", b"\x1b[2K".as_slice(), 0),
        ("ECH", b"\x1b[X".as_slice(), 0),
        ("ICH", b"\x1b[@".as_slice(), 0),
        ("DCH", b"\x1b[P".as_slice(), 3),
    ] {
        let mut terminal = Terminal::new(4, 1).expect("terminal");
        output(&mut terminal, 0, b"AB").expect("default text");
        let mut edit = RICH.to_vec();
        edit.extend_from_slice(b"\x1b[1;1H");
        edit.extend_from_slice(command);
        output(&mut terminal, 1, &edit).expect(name);
        let blank = terminal.cell(0, inspected_column).expect("new blank");
        assert_eq!(blank.character, ' ', "{name}");
        assert_eq!(
            blank.attributes,
            Attributes {
                foreground: Color::Indexed(1),
                background: Color::Indexed(4),
                ..Attributes::default()
            },
            "{name}"
        );
    }

    for (name, command) in [("IL", b"\x1b[L".as_slice()), ("DL", b"\x1b[M".as_slice())] {
        let mut terminal = Terminal::new(3, 2).expect("terminal");
        output(&mut terminal, 0, b"A\x1b[2;1HB").expect("two rows");
        let mut edit = RICH.to_vec();
        edit.extend_from_slice(b"\x1b[1;1H");
        edit.extend_from_slice(command);
        output(&mut terminal, 1, &edit).expect(name);
        let row = if name == "IL" { 0 } else { 1 };
        let blank = terminal.cell(row, 0).expect("new line blank");
        assert_eq!(blank.character, ' ');
        assert_eq!(
            blank.attributes,
            Attributes {
                foreground: Color::Indexed(1),
                background: Color::Indexed(4),
                ..Attributes::default()
            }
        );
    }

    let mut rectangles = Terminal::new(4, 1).expect("terminal");
    let mut setup = RICH.to_vec();
    setup.extend_from_slice(b"AB");
    output(&mut rectangles, 0, &setup).expect("rich rectangle cells");
    output(&mut rectangles, 1, b"\x1b[1;1;1;1$z").expect("DECERA with rich current attributes");
    assert_eq!(
        rectangles.cell(0, 0).expect("DECERA blank").attributes,
        Attributes {
            foreground: Color::Indexed(1),
            background: Color::Indexed(4),
            ..Attributes::default()
        }
    );
    let preserved = rectangles.cell(0, 1).expect("DECSERA source").attributes;
    output(&mut rectangles, 2, b"\x1b[0m\x1b[1;2;1;2${")
        .expect("DECSERA retains original cell attributes");
    let selective = rectangles.cell(0, 1).expect("DECSERA blank");
    assert_eq!(selective.character, ' ');
    assert_eq!(selective.attributes, preserved);

    let mut fill = RICH.to_vec();
    fill.extend_from_slice(b"\x1b[1\"q\x1b[88;1;3;1;3$x");
    output(&mut rectangles, 3, &fill).expect("DECFRA uses complete current presentation");
    let filled = rectangles.cell(0, 2).expect("DECFRA X");
    assert_eq!(filled.character, 'X');
    assert!(filled.attributes.faint && filled.attributes.overline);
    assert_eq!(filled.attributes.underline_style, UnderlineStyle::Dotted);
    assert_eq!(filled.attributes.underline_color, Color::Indexed(6));
    assert!(filled.protected);

    output(&mut rectangles, 4, b"\x1bc").expect("RIS");
    for column in 0..rectangles.dimensions().0 {
        let cell = rectangles.cell(0, column).expect("RIS blank");
        assert_eq!(cell, Cell::default());
    }
}

#[test]
fn standard_erase_edits_and_disabling_autowrap_cancel_delayed_wrap() {
    for (name, command) in [
        ("ED", b"\x1b[2J".as_slice()),
        ("EL", b"\x1b[2K".as_slice()),
        ("ECH", b"\x1b[X".as_slice()),
        ("ICH", b"\x1b[@".as_slice()),
        ("DCH", b"\x1b[P".as_slice()),
        ("IL", b"\x1b[L".as_slice()),
        ("DL", b"\x1b[M".as_slice()),
        ("DECAWM reset", b"\x1b[?7l".as_slice()),
    ] {
        let mut terminal = Terminal::new(2, 2).expect("terminal");
        output(&mut terminal, 0, b"AB").expect("establish delayed wrap");
        let mut suffix = command.to_vec();
        suffix.push(b'X');
        output(&mut terminal, 1, &suffix).expect(name);
        assert_eq!(
            terminal.cell(0, 1).expect("same-row cursor cell").character,
            'X',
            "{name}"
        );
    }
}

#[test]
fn decsace_survives_snapshot_alternate_and_resize_but_ris_resets_it() {
    let mut direct = Terminal::new(4, 2).expect("terminal");
    output(&mut direct, 0, b"\x1b[2*x").expect("rectangle extent");
    assert_eq!(
        direct.modes.attribute_change_extent,
        AttributeChangeExtent::Rectangle
    );
    let snapshot = direct.export_snapshot_v2().expect("DECSACE checkpoint");
    let mut restored = Terminal::import_snapshot_v2(&snapshot).expect("DECSACE restore");
    assert_eq!(
        restored.modes.attribute_change_extent,
        AttributeChangeExtent::Rectangle
    );

    output(&mut direct, 1, b"\x1b[?1049h").expect("direct alternate");
    output(&mut restored, 1, b"\x1b[?1049h").expect("restored alternate");
    for terminal in [&mut direct, &mut restored] {
        terminal
            .apply_event(Event::Resize {
                source_sequence: 2,
                columns: 6,
                rows: 3,
            })
            .expect("resize in alternate");
        output(terminal, 3, b"\x1b[?1049l").expect("leave alternate");
        assert_eq!(
            terminal.modes.attribute_change_extent,
            AttributeChangeExtent::Rectangle
        );
    }
    assert_eq!(restored, direct);

    output(&mut direct, 4, b"\x1bc").expect("direct RIS");
    output(&mut restored, 4, b"\x1bc").expect("restored RIS");
    assert_eq!(
        direct.modes.attribute_change_extent,
        AttributeChangeExtent::Stream
    );
    assert_eq!(restored, direct);
    assert_snapshot_self_closure(&restored);
}

#[test]
fn output_profile_defaults_to_utf8_and_explicit_utf8_preserves_legacy_c1_bytes() {
    let mut default = Terminal::new(8, 2).expect("default terminal");
    let mut explicit =
        Terminal::new_with_output_profile(8, 2, 0, TerminalOptions::default(), OutputProfile::Utf8)
            .expect("explicit UTF-8 terminal");
    assert_eq!(default.output_profile(), OutputProfile::Utf8);
    assert_eq!(explicit.output_profile(), OutputProfile::Utf8);

    let bytes = b"\x9b2J";
    output(&mut default, 0, bytes).expect("legacy C1-range text");
    output(&mut explicit, 0, bytes).expect("explicit legacy C1-range text");
    assert_eq!(default, explicit);
    assert_eq!(
        default.cell(0, 0).expect("replacement cell").character,
        '\u{fffd}'
    );
    assert_eq!(
        default.cell(0, 1).expect("literal parameter").character,
        '2'
    );
    assert_eq!(default.cell(0, 2).expect("literal final").character, 'J');
}

#[test]
fn all_supported_standalone_c1_aliases_match_their_seven_bit_forms() {
    let cases: [(u8, &[u8], &[u8]); 13] = [
        (0x84, b"\x1b[1;1H\x1bD", b"\x1b[1;1H\x84"),
        (0x85, b"\x1b[1;2H\x1bE", b"\x1b[1;2H\x85"),
        (0x88, b"\x1b[1;3H\x1bH", b"\x1b[1;3H\x88"),
        (0x8d, b"\x1b[2;1H\x1bM", b"\x1b[2;1H\x8d"),
        (0x8e, b"\x1bN", b"\x8e"),
        (0x8f, b"\x1bO", b"\x8f"),
        (0x90, b"\x1bP1zignored\x1b\\", b"\x901zignored\x1b\\"),
        (0x98, b"\x1bXignored\x1b\\", b"\x98ignored\x1b\\"),
        (0x9b, b"\x1b[2;3H", b"\x9b2;3H"),
        (0x9c, b"\x1b\\", b"\x9c"),
        (0x9d, b"\x1b]2;title\x1b\\", b"\x9d2;title\x1b\\"),
        (0x9e, b"\x1b^ignored\x1b\\", b"\x9eignored\x1b\\"),
        (0x9f, b"\x1b_ignored\x1b\\", b"\x9fignored\x1b\\"),
    ];

    for (c1, seven_bit, eight_bit) in cases {
        let mut canonical = c1_terminal(12, 3);
        let mut alias = c1_terminal(12, 3);
        output(&mut canonical, 0, seven_bit).expect("seven-bit control form");
        output(&mut alias, 0, eight_bit)
            .unwrap_or_else(|error| panic!("standalone C1 0x{c1:02x} failed: {error}"));
        assert_eq!(alias, canonical, "standalone C1 0x{c1:02x}");
    }
}

#[test]
fn unsupported_standalone_c1_values_fail_closed_atomically() {
    let unsupported = [
        0x80, 0x81, 0x82, 0x83, 0x86, 0x87, 0x89, 0x8a, 0x8b, 0x8c, 0x91, 0x92, 0x93, 0x94, 0x95,
        0x96, 0x97, 0x99, 0x9a,
    ];
    for c1 in unsupported {
        let mut terminal = c1_terminal(8, 2);
        let mut expected = terminal.clone();
        expected.failed_closed = true;
        assert_eq!(
            output(&mut terminal, 0, &[c1]),
            Err(TerminalError::Unsupported("escape sequence")),
            "standalone C1 0x{c1:02x}"
        );
        assert_eq!(terminal, expected, "standalone C1 0x{c1:02x}");
    }
}

#[test]
fn standalone_c1_batch_failure_rolls_back_earlier_events_before_poisoning() {
    let mut terminal = c1_terminal(8, 2);
    let mut expected = terminal.clone();
    expected.failed_closed = true;
    let events = [
        Event::Output {
            source_sequence: 0,
            bytes: b"committed only in candidate",
        },
        Event::Output {
            source_sequence: 1,
            bytes: b"\x80",
        },
    ];
    assert_eq!(
        terminal.apply_events_atomic(&events),
        Err(TerminalError::Unsupported("escape sequence"))
    );
    assert_eq!(terminal, expected);
}

#[test]
fn standalone_c1_respects_split_utf8_in_ground_and_every_control_string_payload() {
    let mut ground = c1_terminal(12, 2);
    output_one_byte_events(&mut ground, "A😀B".as_bytes()).expect("split Ground emoji");
    assert_eq!(row_text(&ground, 0), "A😀·B        ");
    assert!(!ground.is_parser_incomplete());

    let payload = "名😀".as_bytes();
    let mut strings = Vec::new();
    strings.extend_from_slice(b"\x9d2;");
    strings.extend_from_slice(payload);
    strings.extend_from_slice(b"\x9cO");
    strings.extend_from_slice(b"\x901z");
    strings.extend_from_slice(payload);
    strings.extend_from_slice(b"\x9cD");
    strings.push(0x98);
    strings.extend_from_slice(payload);
    strings.extend_from_slice(b"\x9cS");
    strings.push(0x9e);
    strings.extend_from_slice(payload);
    strings.extend_from_slice(b"\x9cP");
    strings.push(0x9f);
    strings.extend_from_slice(payload);
    strings.extend_from_slice(b"\x9cA");

    let mut terminal = c1_terminal(12, 2);
    output_one_byte_events(&mut terminal, &strings).expect("split UTF-8 control strings");
    assert_eq!(terminal.osc_state().window_title(), "名😀");
    assert_eq!(row_text(&terminal, 0), "ODSPA       ");
    assert!(!terminal.is_parser_incomplete());
}

#[test]
fn utf8_encoded_c1_scalar_in_an_osc_payload_is_not_standalone_st() {
    let bytes = b"\x9d777;A\xc2\x9cB\x9cX";
    let mut terminal = c1_terminal(8, 2);
    output_one_byte_events(&mut terminal, bytes).expect("C2 9C remains OSC payload");
    assert_eq!(row_text(&terminal, 0), "X       ");
    assert!(!terminal.is_parser_incomplete());
}

#[test]
fn utf8_encoded_c1_scalar_in_ground_is_not_reinterpreted_as_standalone_c1() {
    let mut terminal = c1_terminal(8, 2);
    let mut expected = terminal.clone();
    expected.failed_closed = true;
    assert_eq!(
        output(&mut terminal, 0, b"\xc2\x9c"),
        Err(TerminalError::Unsupported("C1 or Unicode control"))
    );
    assert_eq!(terminal, expected);
}

#[test]
fn ris_clears_string_utf8_tracking_but_preserves_the_output_profile() {
    let mut terminal = c1_terminal(8, 2);
    output(&mut terminal, 0, b"\x9d2;\xf0").expect("incomplete OSC emoji lead");
    assert!(terminal.is_parser_incomplete());
    assert_eq!(terminal.string_utf8.remaining, 3);

    output(&mut terminal, 1, b"\x1bc").expect("RIS interrupts the string");
    assert_eq!(
        terminal.output_profile(),
        OutputProfile::Utf8WithStandaloneC1
    );
    assert_eq!(terminal.string_utf8.remaining, 0);
    assert!(!terminal.is_parser_incomplete());
}

#[test]
fn c1_reprocessing_preserves_the_first_action_and_physical_host_effect_offsets() {
    // The explicit ESC leaves the DCS query in DcsEscape. The following C1
    // CSI expands to ESC + '[': its first virtual byte completes the DCS and
    // its second starts CSI. Both parser actions must be observed in order.
    let bytes = b"\x1bP$qm\x1b\x9b5n";
    let c1_offset = u32::try_from(
        bytes
            .iter()
            .position(|&byte| byte == 0x9b)
            .expect("C1 byte")
            + 1,
    )
    .expect("offset fits u32");
    let mut terminal = c1_terminal(8, 2);
    let prepared = terminal
        .prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes,
        })
        .expect("C1 completes DCS then starts CSI");
    let frame = prepared.host_effect_frame_v2();
    assert_eq!(
        host_effect_payloads(frame),
        [b"\x1bP1$r0m\x1b\\".as_slice(), b"\x1b[0n".as_slice()]
    );
    assert_eq!(u64_at(frame, 64 + 8), 0);
    assert_eq!(u32_at(frame, 64 + 16), c1_offset);
    assert_eq!(u32_at(frame, 64 + 20), 0);
    assert_eq!(u64_at(frame, 104 + 8), 0);
    assert_eq!(
        u32_at(frame, 104 + 16),
        u32::try_from(bytes.len()).expect("event length fits u32")
    );
    assert_eq!(u32_at(frame, 104 + 20), 0);
    prepared
        .publish(|_| Ok::<(), ()>(()))
        .expect("publish effects");
    assert_eq!(terminal.next_source_sequence(), 1);
}

#[test]
fn failure_of_the_second_virtual_c1_byte_rolls_back_the_first_action() {
    // The first virtual ESC completes a DCS query and would emit a reply; the
    // second virtual '@' is unsupported. No candidate state or effect frame is
    // made publishable when that second half fails.
    let mut terminal = c1_terminal(8, 2);
    let mut expected = terminal.clone();
    expected.failed_closed = true;
    assert!(matches!(
        terminal.prepare_event_with_host_effects_v2(Event::Output {
            source_sequence: 0,
            bytes: b"\x1bP$qm\x1b\x80",
        }),
        Err(TerminalError::Unsupported("escape sequence"))
    ));
    assert_eq!(terminal, expected);
}

#[test]
fn semantic_mouse_uses_current_dimensions_and_snapshot_restored_modes() {
    let mut terminal = Terminal::new(4, 2).expect("terminal");
    output(&mut terminal, 0, b"\x1b[?1003h\x1b[?1006h").expect("enable SGR any-event mouse");
    let event = InputEvent::Mouse(MouseEvent {
        action: MouseAction::Move,
        position: MousePosition {
            cell: CellPosition { column: 3, row: 1 },
            pixel: None,
        },
        buttons_held: MouseButtons::NONE,
        modifiers: Modifiers::NONE,
    });
    assert_eq!(terminal.encode_input(event), Ok(b"\x1b[<35;4;2M".to_vec()));

    let snapshot = terminal.export_snapshot_v2().expect("mouse mode snapshot");
    let restored = Terminal::import_snapshot_v2(&snapshot).expect("restore mouse mode");
    assert_eq!(restored.encode_input(event), terminal.encode_input(event));

    for outside in [
        MousePosition {
            cell: CellPosition { column: 4, row: 0 },
            pixel: None,
        },
        MousePosition {
            cell: CellPosition { column: 0, row: 2 },
            pixel: None,
        },
    ] {
        let outside_event = InputEvent::Mouse(MouseEvent {
            action: MouseAction::Move,
            position: outside,
            buttons_held: MouseButtons::NONE,
            modifiers: Modifiers::NONE,
        });
        assert!(matches!(
            terminal.encode_input(outside_event),
            Err(InputEncodeError::MouseCellOutOfBounds { .. })
        ));
    }
}
