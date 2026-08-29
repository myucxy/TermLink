package webportal

import (
	"bytes"
	"context"
	"net/http"
	"strings"

	"github.com/termlink/termlink/server/internal/adminroute"
)

type adminRouteContextKey struct{}

func (p *Portal) adminBasePath(ctx context.Context) (string, error) {
	if value, ok := ctx.Value(adminRouteContextKey{}).(string); ok && value != "" {
		return value, nil
	}
	if p.config.AdminRoutes == nil {
		return adminroute.DefaultBasePath, nil
	}
	view, err := p.config.AdminRoutes.Current(ctx)
	if err != nil {
		return "", err
	}
	return view.BasePath, nil
}

func (p *Portal) routeAdminRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if p.config.AdminRoutes == nil {
			next.ServeHTTP(w, r)
			return
		}
		view, err := p.config.AdminRoutes.Current(r.Context())
		if err != nil {
			if isDefaultAdminPath(r.URL.Path) || strings.HasPrefix(r.URL.Path, "/control/") {
				p.renderError(w, http.StatusServiceUnavailable, "管理中心暂不可用", "安全入口配置暂时无法读取，请稍后重试或在服务器本机执行恢复命令。")
				return
			}
			next.ServeHTTP(w, r)
			return
		}
		if !view.Custom {
			next.ServeHTTP(w, r)
			return
		}
		if isDefaultAdminPath(r.URL.Path) {
			p.notFound(w, r)
			return
		}
		if r.URL.Path != view.BasePath && !strings.HasPrefix(r.URL.Path, view.BasePath+"/") {
			next.ServeHTTP(w, r)
			return
		}
		copyRequest := r.Clone(context.WithValue(r.Context(), adminRouteContextKey{}, view.BasePath))
		copyURL := *r.URL
		copyURL.Path = adminroute.DefaultBasePath + strings.TrimPrefix(r.URL.Path, view.BasePath)
		copyRequest.URL = &copyURL
		response := &adminRouteResponseWriter{ResponseWriter: w, basePath: view.BasePath}
		next.ServeHTTP(response, copyRequest)
		response.flush()
	})
}

func isDefaultAdminPath(path string) bool {
	return path == adminroute.DefaultBasePath || strings.HasPrefix(path, adminroute.DefaultBasePath+"/")
}

// adminRouteResponseWriter translates legacy internal /admin links and
// redirects at the final browser boundary. It is used only for an authorized
// request that already arrived through the exact custom prefix.
type adminRouteResponseWriter struct {
	http.ResponseWriter
	basePath string
	status   int
	body     bytes.Buffer
}

func (w *adminRouteResponseWriter) WriteHeader(status int) {
	if w.status == 0 {
		w.status = status
	}
}

func (w *adminRouteResponseWriter) Write(value []byte) (int, error) {
	if w.status == 0 {
		w.status = http.StatusOK
	}
	return w.body.Write(value)
}

func (w *adminRouteResponseWriter) Unwrap() http.ResponseWriter { return w.ResponseWriter }

func (w *adminRouteResponseWriter) flush() {
	if location := w.Header().Get("Location"); strings.HasPrefix(location, adminroute.DefaultBasePath) {
		w.Header().Set("Location", w.basePath+strings.TrimPrefix(location, adminroute.DefaultBasePath))
	}
	w.Header().Del("Content-Length")
	status := w.status
	if status == 0 {
		status = http.StatusOK
	}
	w.ResponseWriter.WriteHeader(status)
	if w.body.Len() > 0 {
		translated := bytes.ReplaceAll(w.body.Bytes(), []byte(`href="/admin`), []byte(`href="`+w.basePath))
		translated = bytes.ReplaceAll(translated, []byte(`action="/admin`), []byte(`action="`+w.basePath))
		_, _ = w.ResponseWriter.Write(translated)
	}
}
