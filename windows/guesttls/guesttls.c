// guesttls.dll: TLS toward the guest for the Windows web proxy, the twin of LightTouchDevice/GuestTLS.c.
//
// The guests are iPhone OS 1 to iOS 7: their Safari and CFNetwork speak TLS 1.0 with the RSA and CBC cipher suites of
// the time, and the proxy answers them on the guestfwd connection it already holds, after a plain-text CONNECT. The Mac
// keeps SecureTransport for this; Windows' own TLS (Schannel) has TLS 1.0 switched off on current releases, so this
// is OpenSSL (the static build windows/scripts/build-usb.sh makes for libimobiledevice), at security level 0, over
// the accepted Winsock socket. The certificate and key arrive as PEM from WebProxyCA.

#include <winsock2.h>
#include <windows.h>

#include <openssl/err.h>
#include <openssl/pem.h>
#include <openssl/ssl.h>

#define EXPORT __declspec(dllexport)

typedef struct GuestTLS {
    SSL_CTX *context;
    SSL *ssl;
} GuestTLS;

static X509 *read_certificate(const char *pem) {
    BIO *bio = BIO_new_mem_buf(pem, -1);
    X509 *certificate = bio ? PEM_read_bio_X509(bio, NULL, NULL, NULL) : NULL;
    BIO_free(bio);
    return certificate;
}

static EVP_PKEY *read_key(const char *pem) {
    BIO *bio = BIO_new_mem_buf(pem, -1);
    EVP_PKEY *key = bio ? PEM_read_bio_PrivateKey(bio, NULL, NULL, NULL) : NULL;
    BIO_free(bio);
    return key;
}

static void release(GuestTLS *tls) {
    if (tls->ssl)
        SSL_free(tls->ssl);
    if (tls->context)
        SSL_CTX_free(tls->context);
    free(tls);
}

/// The handshake on `socket` (blocking) with `certificate_pem`'s leaf and `key_pem`'s key; NULL when it fails.
EXPORT GuestTLS *gtls_accept(SOCKET socket, const char *certificate_pem, const char *key_pem) {
    GuestTLS *tls = calloc(1, sizeof *tls);
    if (!tls)
        return NULL;
    X509 *certificate = read_certificate(certificate_pem);
    EVP_PKEY *key = read_key(key_pem);
    tls->context = SSL_CTX_new(TLS_server_method());
    int ok = tls->context && certificate && key;
    if (ok) {
        // iOS 3's Safari and CFNetwork: TLS 1.0, RSA key exchange, SHA-1 everywhere.
        SSL_CTX_set_security_level(tls->context, 0);
        ok = SSL_CTX_set_min_proto_version(tls->context, TLS1_VERSION)
             && SSL_CTX_set_cipher_list(tls->context, "ALL:@SECLEVEL=0")
             && SSL_CTX_use_certificate(tls->context, certificate) == 1
             && SSL_CTX_use_PrivateKey(tls->context, key) == 1;
        SSL_CTX_set_options(tls->context, SSL_OP_LEGACY_SERVER_CONNECT | SSL_OP_ALLOW_UNSAFE_LEGACY_RENEGOTIATION);
    }
    X509_free(certificate);
    EVP_PKEY_free(key);
    if (ok)
        ok = (tls->ssl = SSL_new(tls->context)) != NULL && SSL_set_fd(tls->ssl, (int)socket) == 1;
    if (ok)
        ok = SSL_accept(tls->ssl) == 1;
    if (!ok) {
        ERR_clear_error();
        release(tls);
        return NULL;
    }
    return tls;
}

/// Up to `max` bytes; 0 at the end of input or on an error.
EXPORT int gtls_read(GuestTLS *tls, void *buffer, int max) {
    int n = SSL_read(tls->ssl, buffer, max);
    if (n <= 0) {
        ERR_clear_error();
        return 0;
    }
    return n;
}

/// All of `count` bytes; 0 when the connection failed first.
EXPORT int gtls_write(GuestTLS *tls, const void *bytes, int count) {
    int offset = 0;
    while (offset < count) {
        int n = SSL_write(tls->ssl, (const char *)bytes + offset, count - offset);
        if (n <= 0) {
            ERR_clear_error();
            return 0;
        }
        offset += n;
    }
    return 1;
}

/// Sends the close notification and frees the session; the socket stays the caller's.
EXPORT void gtls_close(GuestTLS *tls) {
    SSL_shutdown(tls->ssl);
    ERR_clear_error();
    release(tls);
}
