// SPDX-License-Identifier: GPL-3.0-only
#define _DARWIN_C_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/acl.h>
#include <sys/attr.h>
#include <sys/kauth.h>
#include <stddef.h>
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

// Query descriptor-bound extended security. ACL retrieval failures never
// establish absence; accept only a decoded zero-entry/no-ACL declaration.
static int no_extended_acl(int fd) {
    struct attrlist attrs={0};attrs.bitmapcount=ATTR_BIT_MAP_COUNT;
    attrs.commonattr=ATTR_CMN_EXTENDED_SECURITY;
    struct { uint32_t length; attrreference_t reference; unsigned char data[8192]; } result={0};
    if(fgetattrlist(fd,&attrs,&result,sizeof(result),0)<0)return 0;
    if(result.length<sizeof(uint32_t)+sizeof(attrreference_t)||result.length>sizeof(result))return 0;
    if(result.reference.attr_length==0)return 1;
    int64_t offset=(int64_t)sizeof(uint32_t)+result.reference.attr_dataoffset;
    if(offset<(int64_t)(sizeof(uint32_t)+sizeof(attrreference_t))||
       result.reference.attr_length<KAUTH_FILESEC_SIZE(0)||
       (uint64_t)offset+result.reference.attr_length>result.length)return 0;
    struct kauth_filesec security;memset(&security,0,sizeof(security));
    memcpy(&security,(unsigned char *)&result+offset,KAUTH_FILESEC_SIZE(0));
    return security.fsec_magic==KAUTH_FILESEC_MAGIC &&
        (security.fsec_entrycount==0 || security.fsec_entrycount==KAUTH_FILESEC_NOACL);
}

// AI attachments use private directories and descriptor-relative operations.
int termlink_files_private_directory(int parent, const char *name) {
    if(!name||!*name||strchr(name,'/')||!strcmp(name,".")||!strcmp(name,".."))return -EINVAL;
    if(!no_extended_acl(parent))return -EACCES;
    if(mkdirat(parent,name,0700)<0 && errno!=EEXIST)return -errno;
    int fd=openat(parent,name,O_RDONLY|O_DIRECTORY|O_CLOEXEC|O_NOFOLLOW);
    if(fd<0)return -errno;
    struct stat st;
    if(fstat(fd,&st)<0){int e=errno;close(fd);return -e;}
    if(st.st_uid!=geteuid() || (st.st_mode & 0077) || !no_extended_acl(fd)){close(fd);return -EACCES;}
    return fd;
}
int termlink_files_write_child(int parent, const char *name, int create) {
    if(!name||!*name||strchr(name,'/')||!strcmp(name,".")||!strcmp(name,".."))return -EINVAL;
    if(!no_extended_acl(parent))return -EACCES;
    int flags=O_RDWR|O_CLOEXEC|O_NONBLOCK|O_NOFOLLOW;
    if(create)flags|=O_CREAT|O_EXCL;
    int fd=openat(parent,name,flags,0600);
    if(fd<0)return -errno;
    struct stat st;
    if(fstat(fd,&st)<0){int e=errno;close(fd);return -e;}
    if(!S_ISREG(st.st_mode)||st.st_nlink!=1||st.st_uid!=geteuid()||(st.st_mode&0077)||!no_extended_acl(fd)){close(fd);return -EACCES;}
    return fd;
}
int termlink_files_remove_child(int parent,const char *name) {
    if(!name||!*name||strchr(name,'/')||!strcmp(name,".")||!strcmp(name,".."))return -EINVAL;
    return unlinkat(parent,name,0)<0 ? -errno : 0;
}
int termlink_files_rename_child(int parent,const char *oldname,const char *newname) {
    if(!oldname||!newname||!*oldname||!*newname||strchr(oldname,'/')||strchr(newname,'/')||
       !strcmp(oldname,".")||!strcmp(oldname,"..")||!strcmp(newname,".")||!strcmp(newname,".."))return -EINVAL;
    return renameatx_np(parent,oldname,parent,newname,RENAME_EXCL)<0 ? -errno : 0;
}
