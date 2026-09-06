package identity

import "testing"

func TestPasswordDigestUsesArgon2idAndUniqueSalt(t *testing.T) {
	first, err := hashPassword("correct horse battery staple")
	if err != nil {
		t.Fatal(err)
	}
	second, err := hashPassword("correct horse battery staple")
	if err != nil {
		t.Fatal(err)
	}
	if string(first.Salt) == string(second.Salt) || string(first.Hash) == string(second.Hash) {
		t.Fatal("password digests reused salt or hash material")
	}
	if !verifyPassword("correct horse battery staple", first) {
		t.Fatal("valid password was rejected")
	}
	if verifyPassword("incorrect password", first) {
		t.Fatal("invalid password was accepted")
	}
}

func TestPasswordDigestRejectsUnsupportedInputAndParameters(t *testing.T) {
	if _, err := hashPassword("short"); err == nil {
		t.Fatal("short password was accepted")
	}
	digest, err := hashPassword("valid password value")
	if err != nil {
		t.Fatal(err)
	}
	digest.MemoryKiB = 1
	if verifyPassword("valid password value", digest) {
		t.Fatal("unsafe stored parameters were accepted")
	}
}
