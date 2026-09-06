package identity

import (
	"crypto/rand"
	"crypto/subtle"
	"errors"

	"golang.org/x/crypto/argon2"
)

const (
	passwordSaltBytes   = 16
	passwordHashBytes   = 32
	passwordMemoryKiB   = 64 * 1024
	passwordIterations  = 3
	passwordParallelism = 2
)

type passwordDigest struct {
	Hash        []byte
	Salt        []byte
	MemoryKiB   uint32
	Iterations  uint32
	Parallelism uint8
}

func hashPassword(password string) (passwordDigest, error) {
	if len(password) < 8 || len(password) > 1024 {
		return passwordDigest{}, errors.New("password is outside the supported range")
	}
	salt := make([]byte, passwordSaltBytes)
	if _, err := rand.Read(salt); err != nil {
		return passwordDigest{}, err
	}
	passwordBytes := []byte(password)
	defer clear(passwordBytes)
	hash := argon2.IDKey(passwordBytes, salt, passwordIterations, passwordMemoryKiB, passwordParallelism, passwordHashBytes)
	return passwordDigest{
		Hash: hash, Salt: salt, MemoryKiB: passwordMemoryKiB,
		Iterations: passwordIterations, Parallelism: passwordParallelism,
	}, nil
}

func verifyPassword(password string, digest passwordDigest) bool {
	if len(digest.Hash) != passwordHashBytes || len(digest.Salt) != passwordSaltBytes ||
		digest.MemoryKiB < 32*1024 || digest.MemoryKiB > 256*1024 ||
		digest.Iterations < 2 || digest.Iterations > 10 ||
		digest.Parallelism < 1 || digest.Parallelism > 8 || len(password) > 1024 {
		return false
	}
	passwordBytes := []byte(password)
	defer clear(passwordBytes)
	actual := argon2.IDKey(passwordBytes, digest.Salt, digest.Iterations, digest.MemoryKiB, digest.Parallelism, uint32(len(digest.Hash)))
	defer clear(actual)
	return subtle.ConstantTimeCompare(actual, digest.Hash) == 1
}
