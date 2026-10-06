"""Negative controls for verify-console-trends.sh's exact collector teardown policy."""

import pathlib
import tempfile

from collector_cleanup_policy import probe_process, trusted_paths


def main():
    with tempfile.TemporaryDirectory(prefix="desktop-collector-policy-") as temporary:
        root = pathlib.Path(temporary).resolve()
        installed = root / "Avalonia.BuildServices.Collector.dll"
        installed.write_bytes(b"test marker")

        def exact_ps(command):
            if command[2] == "pid=,pgid=,comm=":
                return "4242 4242 dotnet\n"
            return f"dotnet exec --runtimeconfig {root / 'runtimeconfig.json'} {installed}\n"

        valid = probe_process("4242", "4242", exact_ps, root, installed)
        assert valid == "verified", valid

        def unknown_child_ps(command):
            if command[2] == "pid=,pgid=,comm=":
                return "4242 4242 dotnet\n"
            return "dotnet build StyloMail.slnx\n"

        unknown_child = probe_process("4242", "4242", unknown_child_ps, root, installed)
        assert unknown_child == "exec-shape-mismatch", unknown_child

        with tempfile.TemporaryDirectory(prefix="desktop-wrong-collector-") as outside:
            wrong = pathlib.Path(outside) / "Avalonia.BuildServices.Collector.dll"
            wrong.write_bytes(b"not installed package")

            def wrong_path_ps(command):
                if command[2] == "pid=,pgid=,comm=":
                    return "4242 4242 dotnet\n"
                return f"dotnet exec --runtimeconfig {root / 'runtimeconfig.json'} {wrong}\n"

            wrong_path = probe_process("4242", "4242", wrong_path_ps, root, installed)
            assert wrong_path == "collector-path-outside-trusted-root", wrong_path

        def unavailable_ps(_command):
            raise OSError("synthetic unavailable process probe")

        unavailable = probe_process("4242", "4242", unavailable_ps, root, installed)
        assert unavailable == "identity-probe-unavailable", unavailable

        wrong_group = probe_process("4242", "9999", exact_ps, root, installed)
        assert wrong_group == "pgid-mismatch", wrong_group

        wrong_executable = probe_process(
            "4242", "4242", lambda _command: "4242 4242 not-dotnet\n", root, installed
        )
        assert wrong_executable == "executable-mismatch", wrong_executable

    package_root, collector = trusted_paths()
    print("collector policy controls: exact=verified, unknown=fail-closed, wrong-path=fail-closed, unavailable=fail-closed")
    print(f"trusted installed package target: root={package_root.name}, file={collector.name}")


if __name__ == "__main__":
    main()
