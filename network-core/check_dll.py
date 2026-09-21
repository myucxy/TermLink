paths = [
    'target/release/termlink_direct_ffi.dll',
    'target/x86_64-pc-windows-msvc/release/termlink_direct_ffi.dll',
]
for path in paths:
    try:
        data = open(path, 'rb').read()
    except OSError as error:
        print(path, 'missing:', error)
        continue
    print(path, '| size:', len(data), '| has renew_apply:',
          b'termlink_direct_renew_apply_v5' in data)
