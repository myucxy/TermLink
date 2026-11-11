// SPDX-License-Identifier: GPL-3.0-only
#define _DARWIN_C_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

// Paths are opened one component at a time with no-follow. Handles stay bound
// to the opened directory even if a local process renames/replaces its path.
static int kind(mode_t mode) { return S_ISDIR(mode) ? 1 : S_ISREG(mode) ? 2 : S_ISLNK(mode) ? 3 : 4; }
static void info(const struct stat *s, int *type, int64_t *size, int64_t *mtime) {
    *type=kind(s->st_mode); *size=s->st_size; *mtime=s->st_mtimespec.tv_sec;
}
int termlink_files_open_child(int parent, const char *name, int *type, int64_t *size, int64_t *mtime) {
    if (!name || !*name || strchr(name,'/') || !strcmp(name,".") || !strcmp(name,"..")) return -EINVAL;
    int fd=openat(parent,name,O_RDONLY|O_CLOEXEC|O_NONBLOCK|O_NOFOLLOW);
    if(fd<0)return -errno;
    struct stat st;
    if(fstat(fd,&st)<0){int e=errno;close(fd);return -e;}
    if(!S_ISDIR(st.st_mode)&&!S_ISREG(st.st_mode)){close(fd);return -ENOTSUP;}
    info(&st,type,size,mtime);return fd;
}
int termlink_files_open(const char *path, int *type, int64_t *size, int64_t *mtime) {
    if(!path||path[0]!='/'||strlen(path)>4096)return -EINVAL;
    int fd=open("/",O_RDONLY|O_DIRECTORY|O_CLOEXEC);
    if(fd<0)return -errno;
    char *copy=strdup(path),*state=NULL;
    if(!copy){close(fd);return -ENOMEM;}
    for(char *part=strtok_r(copy,"/",&state);part;part=strtok_r(NULL,"/",&state)){
        int next=termlink_files_open_child(fd,part,type,size,mtime);
        close(fd);fd=next;
        if(fd<0){free(copy);return fd;}
    }
    free(copy);struct stat st;
    if(fstat(fd,&st)<0){int e=errno;close(fd);return -e;}
    info(&st,type,size,mtime);return fd;
}
void *termlink_files_list_begin(int fd) {
    int copy=fcntl(fd,F_DUPFD_CLOEXEC,0);if(copy<0)return NULL;
    DIR *d=fdopendir(copy);if(!d)close(copy);return d;
}
int termlink_files_list_next(void *iterator,char *name,size_t capacity,int *type,int64_t *size,int64_t *mtime) {
    if(!iterator||!name||capacity<2)return -EINVAL;
    struct dirent *entry;
    for(;;){
        errno=0;entry=readdir(iterator);
        if(!entry)return errno ? -errno : 0;
        if(strcmp(entry->d_name,".")&&strcmp(entry->d_name,".."))break;
    }
    size_t length=strlen(entry->d_name);if(length>=capacity)return -ENAMETOOLONG;
    memcpy(name,entry->d_name,length+1);
    struct stat st;
    if(fstatat(dirfd(iterator),entry->d_name,&st,AT_SYMLINK_NOFOLLOW)<0)return -errno;
    info(&st,type,size,mtime);return 1;
}
void termlink_files_list_end(void *iterator){if(iterator)closedir(iterator);}
int termlink_files_close(int fd){return close(fd);}

int termlink_files_same(int left,int right) {
    struct stat a,b;
    if(fstat(left,&a)<0||fstat(right,&b)<0)return -errno;
    return a.st_dev==b.st_dev&&a.st_ino==b.st_ino;
}
