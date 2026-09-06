package identity

import (
	"bytes"
	"errors"
	"io"
	"net/http"
	"net/url"
	"strings"
)

// inProcessRoundTripper lets the web portal exchange an authorization code
// with the issuer hosted by the same process before or without hairpin DNS.
// It accepts only the configured public origin and identity path.
type inProcessRoundTripper struct {
	handler http.Handler
	origin  string
}

func (t inProcessRoundTripper) RoundTrip(request *http.Request) (*http.Response, error) {
	if request == nil || t.handler == nil {
		return nil, errors.New("embedded identity transport is unavailable")
	}
	origin, err := url.Parse(t.origin)
	if err != nil || request.URL == nil || !strings.EqualFold(request.URL.Scheme, origin.Scheme) ||
		!strings.EqualFold(request.URL.Host, origin.Host) || !strings.HasPrefix(request.URL.Path, "/realms/termlink/") {
		return nil, errors.New("embedded identity transport rejected an external target")
	}
	clone := request.Clone(request.Context())
	clone.RequestURI = request.URL.RequestURI()
	recorder := &inProcessResponse{header: make(http.Header), status: http.StatusOK}
	t.handler.ServeHTTP(recorder, clone)
	response := &http.Response{
		StatusCode: recorder.status,
		Status:     http.StatusText(recorder.status),
		Header:     recorder.header.Clone(),
		Body:       io.NopCloser(bytes.NewReader(recorder.body.Bytes())),
		Request:    request,
	}
	return response, nil
}

type inProcessResponse struct {
	header http.Header
	body   bytes.Buffer
	status int
	wrote  bool
}

func (r *inProcessResponse) Header() http.Header { return r.header }

func (r *inProcessResponse) WriteHeader(status int) {
	if r.wrote {
		return
	}
	r.status, r.wrote = status, true
}

func (r *inProcessResponse) Write(data []byte) (int, error) {
	if !r.wrote {
		r.WriteHeader(http.StatusOK)
	}
	return r.body.Write(data)
}
