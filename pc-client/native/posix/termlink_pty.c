#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif

#if defined(_WIN32)
#define TERMLINK_EXPORT __declspec(dllexport)
#else
#define TERMLINK_EXPORT __attribute__((visibility("default")))
#endif

/*
 * forkpty must not return through a managed runtime in the child. This small
 * native boundary performs only async-signal-safe setup between fork and
 * execve; the .NET process receives the PTY master and child PID in the parent.
 */
TERMLINK_EXPORT int termlink_pty_spawn(
    const char *executable,
    const char *working_directory,
    char *const argv[],
    char *const envp[],
    uint16_t columns,
    uint16_t rows,
    int *master_fd,
    int *child_pid)
{
    if (executable == NULL || executable[0] == '\0' ||
        working_directory == NULL || working_directory[0] == '\0' ||
        argv == NULL || envp == NULL || master_fd == NULL || child_pid == NULL ||
        columns == 0 || rows == 0)
    {
        return -EINVAL;
    }

    struct winsize size;
    size.ws_row = rows;
    size.ws_col = columns;
    size.ws_xpixel = 0;
    size.ws_ypixel = 0;

    int master = -1;
    pid_t pid = forkpty(&master, NULL, NULL, &size);
    if (pid < 0)
    {
        return -errno;
    }
    if (pid == 0)
    {
        if (chdir(working_directory) != 0)
        {
            _exit(126);
        }
        execve(executable, argv, envp);
        _exit(errno == ENOENT ? 127 : 126);
    }

    int flags = fcntl(master, F_GETFL, 0);
    if (flags < 0 || fcntl(master, F_SETFL, flags | O_NONBLOCK) != 0)
    {
        int failure = errno;
        close(master);
        kill((pid_t)-pid, SIGKILL);
        waitpid(pid, NULL, 0);
        return -failure;
    }

    *master_fd = master;
    *child_pid = (int)pid;
    return 0;
}

TERMLINK_EXPORT int termlink_pty_resize(int master_fd, uint16_t columns, uint16_t rows)
{
    if (master_fd < 0 || columns == 0 || rows == 0)
    {
        return -EINVAL;
    }
    struct winsize size;
    size.ws_row = rows;
    size.ws_col = columns;
    size.ws_xpixel = 0;
    size.ws_ypixel = 0;
    if (ioctl(master_fd, TIOCSWINSZ, &size) != 0)
    {
        return -errno;
    }
    return 0;
}

TERMLINK_EXPORT int termlink_pty_signal_group(int child_pid, int signal_number)
{
    if (child_pid <= 0 || signal_number <= 0)
    {
        return -EINVAL;
    }
    if (kill((pid_t)-child_pid, signal_number) != 0)
    {
        return errno == ESRCH ? 0 : -errno;
    }
    return 0;
}

/* Returns 1 when reaped, 0 while running, or a negative errno. */
TERMLINK_EXPORT int termlink_pty_try_wait(int child_pid, int *exit_code)
{
    if (child_pid <= 0 || exit_code == NULL)
    {
        return -EINVAL;
    }
    int status = 0;
    pid_t result = waitpid((pid_t)child_pid, &status, WNOHANG);
    if (result == 0)
    {
        return 0;
    }
    if (result < 0)
    {
        return errno == ECHILD ? 1 : -errno;
    }
    if (WIFEXITED(status))
    {
        *exit_code = WEXITSTATUS(status);
    }
    else if (WIFSIGNALED(status))
    {
        *exit_code = 128 + WTERMSIG(status);
    }
    else
    {
        *exit_code = 255;
    }
    return 1;
}
