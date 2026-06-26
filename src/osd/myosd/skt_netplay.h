// license:BSD-3-Clause
// copyright-holders: David Valdeita (Seleuco) 
/***************************************************************************

    skt_netplay.h

    MAME4droid Netplay Architecture

***************************************************************************/

#ifndef skt_netplay_h
#define skt_netplay_h


#include <netdb.h>
        
    typedef struct skt_netplay
    {
        struct addrinfo *addr;
        int fd;
        struct sockaddr_storage client_addr;
        socklen_t client_addr_len;
        int has_client_addr;
        
    }skt_netplay_t;
    
    
    int skt_netplay_init(netplay_t *handle,const char *server, uint16_t port,void (*warn_cb)(char *));
    int skt_netplay_get_address(const char *name, char*ip);
    
    

    
#endif