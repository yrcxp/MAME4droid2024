// license:BSD-3-Clause
// copyright-holders: David Valdeita (Seleuco) 
/***************************************************************************

    skt_netplay.cpp

    MAME4droid Netplay Architecture

***************************************************************************/

#include <sys/socket.h>
#include <netdb.h>

#include <arpa/inet.h>
#include <net/if.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/types.h>
#include <errno.h>

#include <signal.h>

#include <unistd.h>

#include <pthread.h>

#include <stdio.h>
#include <string.h>

#ifndef ANDROID
#include <ifaddrs.h>
#else
//#include <ifaddrs-android.h>
#endif

#include "netplay.h"
#include "skt_netplay.h"
#include <android/log.h>

#define NLOG(...) __android_log_print(ANDROID_LOG_DEBUG, "MAME4droid_Netplay", __VA_ARGS__)
#define NLOG_VERBOSE(...) do { if(0) __android_log_print(ANDROID_LOG_DEBUG, "MAME4droid_Netplay", __VA_ARGS__); } while(0)

static skt_netplay_t skt_netplay_impl;
static pthread_t main_tid;

static int skt_init_handle_impl(skt_netplay_t *impl){
    
    memset(impl,0,sizeof(skt_netplay_impl));
    
    impl->fd = -1;
    
    return 1;
}

static skt_netplay_t * skt_get_handle_impl(){
    static int init = 0;
    if(!init)
    {
        skt_init_handle_impl(&skt_netplay_impl);
        
        signal(SIGPIPE, SIG_IGN); // Do not like SIGPIPE killing our app :(

        init = 1;
    }
    return &skt_netplay_impl;
}

static int skt_init_udp_socket(netplay_t *handle, const char *server, uint16_t port)
{
    struct addrinfo hints;
    memset(&hints, 0, sizeof(hints));
        
    skt_netplay_t *impl = (skt_netplay_t *)handle->impl_data;
    
    hints.ai_family = AF_INET;    
    hints.ai_socktype = SOCK_DGRAM;
    if (!server)
        hints.ai_flags = AI_PASSIVE;
    
    char port_buf[16];
    snprintf(port_buf, sizeof(port_buf), "%hu", (unsigned short)port);
    if (getaddrinfo(server, port_buf, &hints, &impl->addr) < 0)
        return 0;
    
    if (!impl->addr)
        return 0;
    
    impl->fd = socket(impl->addr->ai_family, impl->addr->ai_socktype, impl->addr->ai_protocol);
    if (impl->fd < 0)
    {
        NLOG("socket() failed: %s", strerror(errno));
        return 0;
    }

    /* Enlarge kernel socket buffers to absorb burst packet loss on
     * mobile Wi-Fi hotspots.  The default on Android is often only 212 KB
     * receive / 112 KB send; under jitter bursts the kernel drops packets
     * before the network thread can read them.                            */
    {
        int rcvbuf = 256 * 1024;   /* 256 KB receive                      */
        int sndbuf = 128 * 1024;   /* 128 KB send                         */
        setsockopt(impl->fd, SOL_SOCKET, SO_RCVBUF, &rcvbuf, sizeof(rcvbuf));
        setsockopt(impl->fd, SOL_SOCKET, SO_SNDBUF, &sndbuf, sizeof(sndbuf));
    }

    if (!server)
    {
        int yes = 1;
        setsockopt(impl->fd, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(int));
        
        if (bind(impl->fd, impl->addr->ai_addr, impl->addr->ai_addrlen) < 0)
        {
            char buf[256];
            sprintf(buf,"Failed to bind socket.\nError: %s\n",strerror(errno));
            NLOG("bind() failed: %s", strerror(errno));
            handle->netplay_warn(buf);
            close(impl->fd);
            impl->fd = -1;
        } else {
            struct sockaddr_in *sin = (struct sockaddr_in *)impl->addr->ai_addr;
            char *ip = inet_ntoa(sin->sin_addr);
            int bound_port = ntohs(sin->sin_port);
            NLOG("bind() succeeded on fd %d. Bound to IP: %s, Port: %d", impl->fd, ip, bound_port);
        }
        
        freeaddrinfo(impl->addr);
        impl->addr = NULL;
        if(impl->fd == -1)
            return 0;
    } else {
        NLOG("socket() succeeded on fd %d for client", impl->fd);
    }
    
    return 1;
}

/*
static int skt_init_tcp_socket(netplay_t *handle, const char *server, uint16_t port)
{
    struct addrinfo hints;
    skt_netplay_t *impl = (skt_netplay_t *)handle->impl_data;
    
    memset(&hints, 0, sizeof(hints));
    
    hints.ai_family = AF_INET;    
    hints.ai_socktype = SOCK_STREAM;
    if (!server)
        hints.ai_flags = AI_PASSIVE;
    
    char port_buf[16];
    snprintf(port_buf, sizeof(port_buf), "%hu", (unsigned short)port);
    if (getaddrinfo(server, port_buf, &hints, &impl->addr) < 0)
        return 0;
    
    if (!impl->addr)
        return 0;
    
    if (!server)
    {
        int new_fd;
        
        while (impl->addr)
        {
            
            impl->fd = socket(impl->addr->ai_family, impl->addr->ai_socktype, impl->addr->ai_protocol);
            if (impl->fd < 0)
            {
                printf("Failed to init socket...\n");
                return 0;
            }
            
            int yes = 1;
            setsockopt(impl->fd, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(int));
            if(setsockopt(impl->fd, IPPROTO_TCP, TCP_NODELAY, &yes, sizeof(int))<0)
            {
                printf("an error: %s\n", strerror(errno));
            }
            
            if (bind(impl->fd, impl->addr->ai_addr, impl->addr->ai_addrlen) < 0 ||
                listen(impl->fd, 6))
            {
                printf("Failed to bind socket.\n");
                printf("an error: %s\n", strerror(errno));
                close(impl->fd);
                impl->fd = -1;
            }
            
            if(impl->fd == -1)
                return 0;
            
            socklen_t clilen = sizeof(impl->client_addr);
            
            new_fd = accept(impl->fd, (struct sockaddr*)&impl->client_addr, &clilen);
            if (new_fd < 0)
            {
                printf("Failed to accept socket.\n");
                printf("an error: %s\n", strerror(errno));
            }
            else
            {
                printf(" accept socket.\n");
            }
            
            impl->addr = impl->addr->ai_next;
        }
        
        close(impl->fd);
        impl->fd = new_fd;
        
        freeaddrinfo(impl->addr);
        impl->addr = NULL;
    }
    else
    {
        impl->fd = socket(impl->addr->ai_family, impl->addr->ai_socktype, impl->addr->ai_protocol);
        if (impl->fd < 0)
        {
            printf("Failed to init socket...\n");
            return 0;
        }
        
        if (connect(impl->fd, impl->addr->ai_addr, impl->addr->ai_addrlen) < 0)
        {
            {
                printf("Failed to conect to socket.\n");
                printf("an error: %s\n", strerror(errno));
                close(impl->fd);
                impl->fd = -1;
                return 0;
            }
        }
        else
        {
            printf("conect to socket.\n");
        }
    }
    
    return 1;
}*/

#ifndef ANDROID
int skt_netplay_get_address(const char *name, char*ip)
{
    struct ifaddrs *allInterfaces;
    int find = 0;
    
    // Get list of all interfaces on the local machine:
    if (getifaddrs(&allInterfaces) == 0) {
        struct ifaddrs *interface;
        
        // For each interface ...
        for (interface = allInterfaces; interface != NULL; interface = interface->ifa_next) {
            unsigned int flags = interface->ifa_flags;
            struct sockaddr *addr = interface->ifa_addr;
            
            // Check for running IPv4, IPv6 interfaces. Skip the loopback interface.
            if ((flags & (IFF_UP|IFF_RUNNING|IFF_LOOPBACK)) == (IFF_UP|IFF_RUNNING)) {
                if (addr->sa_family == AF_INET /*|| addr->sa_family == AF_INET6*/) {
                    
                    // Convert interface address to a human readable string:
                    char host[NI_MAXHOST];
                    getnameinfo(addr, sizeof(struct sockaddr_in), host, sizeof(host), NULL, 0, NI_NUMERICHOST);
                    
                    //en0 es WIFI, pdp_ip0 es 3g
                    //printf("address %s %s\n",interface->ifa_name, host);
                    
                    if(strcmp(interface->ifa_name,name)==0)
                    {
                        if(ip!=NULL)
                           strcpy(ip,host);
                        find = 1;
                    }
                }
            }
        }
        freeifaddrs(allInterfaces);
    }
    return find;
}
#endif

static void* skt_threaded_data(void* args)
{
    fd_set fds;
    struct timeval tv = {0};
    tv.tv_sec = 0;
    //tv.tv_usec = 1;
    tv.tv_usec = 500 * 1000;
    
    struct timeval tmp_tv = tv;
    
    netplay_t *handle = (netplay_t *)args;
    skt_netplay_t *impl = (skt_netplay_t *)handle->impl_data;
    
    printf("Creada threaded_data\n");
    
    while(handle->has_connection){
        
        //printf("waiting!\n");
        tmp_tv = tv;
        
        FD_ZERO(&fds);
        FD_SET(impl->fd, &fds);
        
        if (select(impl->fd + 1, &fds, NULL, NULL, &tmp_tv) < 0)
        {
            NLOG("select failed: %s", strerror(errno));
            if (handle->has_connection) {
                handle->has_connection = 0;
                netplay_warn_hangup(handle);
            }
            /* Wake the game thread if it is sleeping in the cond wait
             * so it sees has_connection==0 immediately instead of waiting
             * up to SYNC_TIMEOUT_MS (30 s) for the timedwait to expire.  */
            pthread_mutex_lock(&handle->sync_mutex);
            pthread_cond_broadcast(&handle->sync_cond);
            pthread_mutex_unlock(&handle->sync_mutex);
            break;
        }
        
        if (FD_ISSET(impl->fd, &fds)) // packet arrive
        {
            NLOG_VERBOSE("select unblocked, packet arrived!");
            
            /* Drain loop: Consume ALL pending packets in the kernel buffer 
             * to minimize latency and ensure state freshness. */
            while (1) {
                if(!netplay_read_data(handle))
                {
                    /* Wake up the game thread on recv/parse failure. */
                    pthread_mutex_lock(&handle->sync_mutex);
                    pthread_cond_broadcast(&handle->sync_cond);
                    pthread_mutex_unlock(&handle->sync_mutex);
                    break;
                }
                
                /* Check if more data is available without blocking */
                fd_set drain_fds;
                struct timeval zero_tv = {0, 0};
                FD_ZERO(&drain_fds);
                FD_SET(impl->fd, &drain_fds);
                if (select(impl->fd + 1, &drain_fds, NULL, NULL, &zero_tv) <= 0)
                    break; /* No more packets immediately available */
                if (!FD_ISSET(impl->fd, &drain_fds))
                    break;
            }
        }
    }
    
    close(impl->fd);
    impl->fd = -1;
    
    printf("Muere threaded_data y cierro socket!\n");
    
	return 0;
}

static int skt_read_pkt_data(netplay_t *handle,netplay_msg_t *msg)
{
    skt_netplay_t *impl = (skt_netplay_t *)handle->impl_data;
    socklen_t addrlen = sizeof(impl->client_addr);
    struct sockaddr_storage client_addr;

    int l = recvfrom(impl->fd, msg,  sizeof(netplay_msg_t), 0, (struct sockaddr*)&client_addr, &addrlen);
    
    if (l != sizeof(netplay_msg_t))
    {
        NLOG("recvfrom failed or truncated. l=%d, expected=%lu, error=%s", l, (unsigned long)sizeof(netplay_msg_t), l<0 ? strerror(errno) : "size mismatch");
        if (handle->has_connection) {
            handle->has_connection = 0;
            netplay_warn_hangup(handle);
        }
        return 0;
    }
    
    NLOG_VERBOSE("recvfrom read %d bytes, msg_type=%d", l, msg->msg_type);
    
    if(!impl->has_client_addr)
    {
       memcpy(&impl->client_addr,&client_addr,addrlen);
       impl->client_addr_len = addrlen;
       impl->has_client_addr = 1;
    }

    return 1;
}

static int skt_send_pkt_data(netplay_t *handle,netplay_msg_t *msg)
{
    const struct sockaddr *addr = NULL;
    skt_netplay_t *impl = (skt_netplay_t *)handle->impl_data;
    
    //printf("send_pkt_data!\n");
    
    if (impl->addr)
        addr = impl->addr->ai_addr;
    else if (impl->has_client_addr)
        addr = (const struct sockaddr*)&impl->client_addr;
    
    if (addr)
    {
        socklen_t addr_len = impl->addr ? impl->addr->ai_addrlen : impl->client_addr_len;
        
        struct sockaddr_in *sin = (struct sockaddr_in *)addr;
        char *ip = inet_ntoa(sin->sin_addr);
        int dest_port = ntohs(sin->sin_port);
        NLOG_VERBOSE("sendto about to send to IP: %s, Port: %d", ip, dest_port);

        int l = sendto(impl->fd, msg, sizeof(netplay_msg_t), 0, addr, addr_len);
        if (l != sizeof(netplay_msg_t))
        {
            if (l < 0 && (errno == ENOBUFS || errno == EWOULDBLOCK || errno == EAGAIN))
            {
                NLOG("sendto dropped packet (buffer full). error=%s", strerror(errno));
                return 1; // Just drop the packet, don't disconnect!
            }
            
            NLOG("sendto failed. l=%d, expected=%lu, error=%s", l, (unsigned long)sizeof(netplay_msg_t), l<0 ? strerror(errno) : "size mismatch");
            char buf[256];
            sprintf(buf,"Failed to send data.\nError: %s\n",strerror(errno));
            handle->netplay_warn(buf);
            handle->has_connection = 0;
            return 0;
        }
        NLOG_VERBOSE("sendto sent %d bytes successfully", l);
        //printf("sent target_frame %d peer_frame: %d [uid:%d]\n",handle->target_frame,handle->peer_frame_count,packet_uid);
    }
    return 1;
}

int skt_netplay_init(netplay_t *handle,const char *server, uint16_t port, void (*warn_cb)(char *))
{
    int res = 0;
    
    skt_netplay_t *impl = skt_get_handle_impl();
    
    printf("Init Netplay %s %d\n",server,port);
    
    if(impl->fd != -1)
    {
        usleep(1000 * 1000);//Thread?
        close(impl->fd );//anyway
    }
    
    skt_init_handle_impl(impl);
    
    netplay_init_handle(handle);
    
    handle->impl_data = impl;
    handle->read_pkt_data = skt_read_pkt_data;
    handle->send_pkt_data = skt_send_pkt_data;
    handle->netplay_warn = warn_cb;
    
    handle->player1 = server ? 0 : 1;
    handle->type = NETPLAY_TYPE_SKT;
        
    if (!skt_init_udp_socket(handle, server, port))
        return 0;
    
    handle->has_connection = 1;
    
    /* Create the thread as detached so it self-cleans on exit and we
     * never leak a joinable thread handle across successive skt_netplay_init
     * calls. */
    {
        pthread_attr_t attr;
        pthread_attr_init(&attr);
        pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
        res = pthread_create(&main_tid, &attr, skt_threaded_data, (void *)handle);
        pthread_attr_destroy(&attr);
    }
    if(res!=0)
    {
        printf("Error setting creating pthread %d \n",res);
        close(impl->fd);
        impl->fd = -1;
        return 0;
    }
    
    printf("Conexion creada OK!\n");
    
    return 1;
}

